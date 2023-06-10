﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using TumblThree.Applications.DataModels;
using TumblThree.Applications.DataModels.TumblrPosts;
using TumblThree.Applications.Properties;
using TumblThree.Applications.Services;
using TumblThree.Domain;
using TumblThree.Domain.Models.Blogs;
using TumblThree.Domain.Models.Files;

namespace TumblThree.Applications.Downloader
{
    public abstract class AbstractDownloader : IDownloader, IDisposable
    {
        protected readonly IBlog blog;
        protected readonly IFiles files;
        protected readonly ICrawlerService crawlerService;
        protected readonly IManagerService managerService;
        protected readonly IProgress<DownloadProgress> progress;
        protected readonly object lockObjectDownload = new object();
        protected readonly IPostQueue<AbstractPost> postQueue;
        protected readonly IShellService shellService;
        protected CancellationToken ct;
        protected readonly PauseToken pt;
        protected readonly FileDownloader fileDownloader;
        private readonly string[] suffixes = { ".jpg", ".jpeg", ".png", ".tiff", ".tif", ".heif", ".heic", ".webp" };
        private readonly object _saveTimerLock = new object();
        private Timer _saveTimer;
        private volatile bool _disposed;
        private const int SAVE_TIMESPAN_SECS = 120;

        private SemaphoreSlim concurrentConnectionsSemaphore;
        private SemaphoreSlim concurrentVideoConnectionsSemaphore;
        private readonly Dictionary<string, StreamWriterWithInfo> streamWriters = new Dictionary<string, StreamWriterWithInfo>();
        private readonly object diskFilesLock = new object();
        private HashSet<string> diskFiles;

        protected AbstractDownloader(IShellService shellService, IManagerService managerService, CancellationToken ct, PauseToken pt, IProgress<DownloadProgress> progress, IPostQueue<AbstractPost> postQueue, FileDownloader fileDownloader, ICrawlerService crawlerService = null, IBlog blog = null, IFiles files = null)
        {
            this.shellService = shellService;
            this.crawlerService = crawlerService;
            this.managerService = managerService;
            this.blog = blog;
            this.files = files;
            this.ct = ct;
            this.pt = pt;
            this.progress = progress;
            this.postQueue = postQueue;
            this.fileDownloader = fileDownloader;
            Progress<Exception> prog = new Progress<Exception>((e) => shellService.ShowError(e, Resources.CouldNotSaveBlog, blog.Name));
            _saveTimer = new Timer(_ => OnSaveTimedEvent(prog), null, SAVE_TIMESPAN_SECS * 1000, SAVE_TIMESPAN_SECS * 1000);
        }

        public string AppendTemplate { get; set; }

        public void UpdateProgressQueueInformation(string format, params object[] args)
        {
            var newProgress = new DownloadProgress
            {
                Progress = string.Format(CultureInfo.CurrentCulture, format, args)
            };
            progress.Report(newProgress);
        }

        public void ChangeCancellationToken(CancellationToken ct)
        {
            this.ct = ct;
        }

        protected virtual async Task<(bool result, string fileLocation)> DownloadBinaryFileAsync(string fileLocation, string url)
        {
            try
            {
                return await fileDownloader.DownloadFileWithResumeAsync(url, fileLocation).ConfigureAwait(false);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 0x27 || (ex.HResult & 0xFFFF) == 0x70)
            {
                // Disk Full, HRESULT: ‭-2147024784‬ == 0xFFFFFFFF80070070
                Logger.Error("AbstractDownloader:DownloadBinaryFile: {0}", ex);
                shellService.ShowError(ex, Resources.DiskFull);
                crawlerService.StopCommand.Execute(null);
                throw;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 0x20)
            {
                // The process cannot access the file because it is being used by another process.", HRESULT: -2147024864 == 0xFFFFFFFF80070020
                return (true, fileLocation);
            }
            catch (WebException webException) when (webException.Response != null)
            {
                var webRespStatusCode = (int)((HttpWebResponse)webException.Response).StatusCode;
                if (webRespStatusCode >= 400 && webRespStatusCode < 600) // removes inaccessible files: http status codes 400 to 599
                {
                    try
                    {
                        File.Delete(fileLocation);
                    } // could be open again in a different thread
                    catch
                    {
                    }
                }

                return (false, fileLocation);
            }
            catch (TimeoutException timeoutException)
            {
                Logger.Error("AbstractDownloader:DownloadBinaryFile {0}", timeoutException);
                shellService.ShowError(timeoutException, Resources.TimeoutReached, Resources.Downloading, blog.Name);
                throw;
            }
        }

        protected virtual async Task<(bool result, string fileLocation)> DownloadBinaryFileAsync(string fileLocation, string fileLocationUrlList, string url)
        {
            if (!blog.DownloadUrlList)
            {
                return await DownloadBinaryFileAsync(fileLocation, url);
            }

            return (AppendToTextFile(fileLocationUrlList, url, false), fileLocation);
        }

        protected virtual bool AppendToTextFile(string fileLocation, string text, bool isJson)
        {
            try
            {
                lock (lockObjectDownload)
                {
                    StreamWriterWithInfo sw = GetTextAppenderStreamWriter(fileLocation, isJson);
                    sw.WriteLine(text);
                }
                return true;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 0x27 || (ex.HResult & 0xFFFF) == 0x70)
            {
                Logger.Error("Downloader:AppendToTextFile: {0}", ex);
                shellService.ShowError(ex, Resources.DiskFull);
                crawlerService.StopCommand.Execute(null);
                return false;
            }
            catch
            {
                return false;
            }
        }

        private StreamWriterWithInfo GetTextAppenderStreamWriter(string key, bool isJson)
        {
            if (streamWriters.ContainsKey(key))
            {
                return streamWriters[key];
            }
            StreamWriterWithInfo sw = new StreamWriterWithInfo(key, true, isJson);
            streamWriters.Add(key, sw);

            return sw;
        }

        public virtual async Task<bool> DownloadBlogAsync()
        {
            concurrentConnectionsSemaphore = new SemaphoreSlim(shellService.Settings.ConcurrentConnections / crawlerService.ActiveItems.Count);
            concurrentVideoConnectionsSemaphore = new SemaphoreSlim(shellService.Settings.ConcurrentVideoConnections / crawlerService.ActiveItems.Count);
            var trackedTasks = new List<Task>();
            var completeDownload = true;

            blog.CreateDataFolder();

            await Task.Run(() => Task.CompletedTask);

            try
            {
                while (await postQueue.OutputAvailableAsync(ct))
                {
                    TumblrPost downloadItem = (TumblrPost)await postQueue.ReceiveAsync();

                    if (downloadItem.GetType() == typeof(VideoPost))
                    {
                        await concurrentVideoConnectionsSemaphore.WaitAsync();
                    }

                    await concurrentConnectionsSemaphore.WaitAsync();

                    if (CheckIfShouldStop()) break;

                    CheckIfShouldPause();

                    trackedTasks.Add(DownloadPostAsync(downloadItem));
                }
            }
            catch (OperationCanceledException e)
            {
                System.Diagnostics.Debug.WriteLine(e.ToString());
            }

            // TODO: Is this even right?
            try
            {
                await Task.WhenAll(trackedTasks);
            }
            catch
            {
                completeDownload = false;
            }

            blog.LastDownloadedPhoto = null;
            blog.LastDownloadedVideo = null;

            files.Save();

            return completeDownload;
        }

        private async Task DownloadPostAsync(TumblrPost downloadItem)
        {
            try
            {
                await DownloadPostCoreAsync(downloadItem);
            }
            catch (Exception e)
            {
                Logger.Error("AbstractDownloader.DownloadPostAsync: {0}", e);
            }
            finally
            {
                concurrentConnectionsSemaphore.Release();
                if (downloadItem.GetType() == typeof(VideoPost))
                {
                    concurrentVideoConnectionsSemaphore.Release();
                }
            }
        }

        private async Task DownloadPostCoreAsync(TumblrPost downloadItem)
        {
            // TODO: Refactor, should be polymorphism
            if (downloadItem.PostType == PostType.Binary)
            {
                await DownloadBinaryPostAsync(downloadItem);
            }
            else
            {
                DownloadTextPost(downloadItem);
            }
        }

        public virtual async Task<string> DownloadPageAsync(string url)
        {
            using (Stream s = await fileDownloader.ReadFromUrlIntoStreamAsync(url))
            using (StreamReader sr = new StreamReader(s))
            {
                string content = sr.ReadToEnd();
                return content;
            }
        }

        protected bool CheckIfLinkRestored(TumblrPost downloadItem)
        {
            if (!blog.ForceRescan || blog.FilenameTemplate != "%f") return false;
            lock (diskFilesLock)
            {
                if (diskFiles == null)
                {
                    diskFiles = new HashSet<string>();
                    foreach (var item in Directory.EnumerateFiles(blog.DownloadLocation(), "*", SearchOption.TopDirectoryOnly))
                    {
                        if (!string.Equals(Path.GetExtension(item), ".json", StringComparison.OrdinalIgnoreCase))
                            diskFiles.Add(Path.GetFileName(item).ToLower());
                    }
                }
                var filename = downloadItem.Url.Split('/').Last().ToLower();
                return diskFiles.Contains(filename);
            }
        }

        protected virtual async Task<bool> DownloadBinaryPostAsync(TumblrPost downloadItem)
        {
            if (CheckIfFileExistsInDB(downloadItem))
            {
                string fileName = FileName(downloadItem);
                UpdateProgressQueueInformation(Resources.ProgressSkipFile, fileName);
            }
            else if (!shellService.Settings.LoadAllDatabases && blog.CheckDirectoryForFiles && (blog.CheckIfBlogShouldCheckDirectory(FileNameUrl(downloadItem), FileNameNew(downloadItem))
                || blog.CheckIfBlogShouldCheckDirectory(FileName(downloadItem), FileNameNew(downloadItem))))
            {
                string fileName = AddFileToDb(downloadItem);
                UpdateProgressQueueInformation(Resources.ProgressSkipFile, fileName);
            }
            else if ((shellService.Settings.LoadAllDatabases || !blog.CheckDirectoryForFiles) && CheckIfLinkRestored(downloadItem))
            {
                string fileName = AddFileToDb(downloadItem);
                UpdateProgressQueueInformation(Resources.ProgressSkipFile, fileName);
            }
            else
            {
                string blogDownloadLocation = blog.DownloadLocation();
                string fileName = AddFileToDb(downloadItem);
                string fileLocation = FileLocation(blogDownloadLocation, fileName);
                string fileLocationUrlList = FileLocationLocalized(blogDownloadLocation, downloadItem.TextFileLocation);
                DateTime postDate = PostDate(downloadItem);
                UpdateProgressQueueInformation(Resources.ProgressDownloadImage, fileName);
                bool result;
                (result, fileLocation) = await DownloadBinaryFileAsync(fileLocation, fileLocationUrlList, Url(downloadItem));
                if (!result)
                {
                    return false;
                }

                SetFileDate(fileLocation, postDate);
                UpdateBlogDB(downloadItem.DbType);

                //TODO: Refactor
                if (!shellService.Settings.EnablePreview)
                {
                    return true;
                }

                if (suffixes.Any(suffix => fileName.EndsWith(suffix)))
                {
                    blog.LastDownloadedPhoto = Path.GetFullPath(fileLocation);
                }
                else
                {
                    blog.LastDownloadedVideo = Path.GetFullPath(fileLocation);
                }
                blog.LastPreviewShown = DateTime.Now.Ticks;

                return true;
            }

            return true;
        }

        private void AddTextToDb(TumblrPost downloadItem)
        {
            files.AddFileToDb(PostId(downloadItem), null, downloadItem.Filename);
        }

        protected string AddFileToDb(TumblrPost downloadItem)
        {
            if (AppendTemplate == null)
            {
                files.AddFileToDb(FileNameUrl(downloadItem), FileNameOriginalUrl(downloadItem), downloadItem.Filename);
                return downloadItem.Filename;
            }
            return files.AddFileToDb(FileNameUrl(downloadItem), FileNameOriginalUrl(downloadItem), downloadItem.Filename, AppendTemplate);
        }

        public bool CheckIfFileExistsInDB(string filenameUrl)
        {
            return files.CheckIfFileExistsInDB(filenameUrl, false);
        }

        protected bool CheckIfFileExistsInDB(TumblrPost downloadItem)
        {
            bool found;
            string filenameOrgUrl = string.IsNullOrEmpty(downloadItem.PostedUrl) ? null : FileNameOriginalUrl(downloadItem);
            string filename = FileNameUrl(downloadItem);
            if (shellService.Settings.LoadAllDatabases)
            {
                if (filenameOrgUrl != null)
                {
                    found = managerService.CheckIfFileExistsInDB(filenameOrgUrl, true, shellService.Settings.LoadArchive);
                    if (found || string.IsNullOrEmpty(filename)) return found;
                }
                found = managerService.CheckIfFileExistsInDB(filename, false, shellService.Settings.LoadArchive);
                UpdateLinkIfNeeded(found, filename, filenameOrgUrl);
                return found;
            }

            if (filenameOrgUrl != null)
            {
                found = files.CheckIfFileExistsInDB(filenameOrgUrl, true);
                if (found || string.IsNullOrEmpty(filename)) return found;
            }
            found = files.CheckIfFileExistsInDB(filename, false);
            UpdateLinkIfNeeded(found, filename, filenameOrgUrl);
            return found;
        }

        private void UpdateLinkIfNeeded(bool found, string filename, string filenameOrgUrl)
        {
            if (found && filenameOrgUrl != null)
            {
                // filenameOrgUrl is not equal filename and not found, but filename is found, so update file entry
                files.UpdateOriginalLink(filename, filenameOrgUrl);
            }
        }

        private void DownloadTextPost(TumblrPost downloadItem)
        {
            string postId = PostId(downloadItem);
            if (files.CheckIfFileExistsInDB(postId, false))
            {
                UpdateProgressQueueInformation(Resources.ProgressSkipFile, postId);
            }
            else
            {
                string blogDownloadLocation = blog.DownloadLocation();
                string url = Url(downloadItem);
                string fileLocation = FileLocationLocalized(blogDownloadLocation, downloadItem.TextFileLocation);
                UpdateProgressQueueInformation(Resources.ProgressDownloadImage, postId);
                if (AppendToTextFile(fileLocation, url, blog.MetadataFormat == Domain.Models.MetadataType.Json))
                {
                    UpdateBlogDB(downloadItem.DbType);
                    AddTextToDb(downloadItem);
                }
            }
        }

        protected void UpdateBlogDB(string postType)
        {
            blog.UpdatePostCount(postType);
            blog.UpdateProgress(true);
        }

        protected void SetFileDate(string fileLocation, DateTime postDate)
        {
            if (blog.DownloadUrlList)
            {
                return;
            }

            File.SetLastWriteTime(fileLocation, postDate);
        }

        protected static string Url(TumblrPost downloadItem)
        {
            return downloadItem.Url;
        }

        protected virtual string FileNameUrl(TumblrPost downloadItem)
        {
            return downloadItem.Url?.Split('/').Last();
        }

        protected virtual string FileNameOriginalUrl(TumblrPost downloadItem)
        {
            return downloadItem.PostedUrl?.Split('/').Last();
        }

        protected virtual string FileName(TumblrPost downloadItem)
        {
            string filename = downloadItem.Url.Split('/').Last();
            if (Path.GetExtension(filename).ToLower() == ".gifv")
                filename = Path.GetFileNameWithoutExtension(filename) + ".gif";
            if (Path.GetExtension(filename).ToLower() == ".pnj")
                filename += ".png";
            return filename;
        }

        protected static string FileNameNew(TumblrPost downloadItem)
        {
            return downloadItem.Filename;
        }

        protected static string FileLocation(string blogDownloadLocation, string fileName)
        {
            return Path.Combine(blogDownloadLocation, fileName);
        }

        protected static string FileLocationLocalized(string blogDownloadLocation, string fileName)
        {
            return Path.Combine(blogDownloadLocation, string.Format(CultureInfo.CurrentCulture, fileName));
        }

        private static string PostId(TumblrPost downloadItem)
        {
            return downloadItem.Id;
        }

        protected static DateTime PostDate(TumblrPost downloadItem)
        {
            if (string.IsNullOrEmpty(downloadItem.Date))
            {
                return DateTime.Now;
            }

            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            DateTime postDate = epoch.AddSeconds(Convert.ToDouble(downloadItem.Date)).ToLocalTime();
            return postDate;
        }

        protected bool CheckIfShouldStop()
        {
            return ct.IsCancellationRequested;
        }

        protected void CheckIfShouldPause()
        {
            if (pt.IsPaused)
            {
                pt.WaitWhilePausedWithResponseAsyc().Wait();
            }
        }

        protected void OnSaveTimedEvent(IProgress<Exception> progress)
        {
            lock (_saveTimerLock)
            {
                if (_disposed) return;

                try
                {
                    _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);

                    if (files != null && files.IsDirty) files.Save();
                }
                catch (Exception e)
                {
                    progress.Report(e);
                }
                finally
                {
                    if (!_disposed)
                        _saveTimer.Change(SAVE_TIMESPAN_SECS * 1000, SAVE_TIMESPAN_SECS * 1000);
                }
            }
        }

        public virtual bool CheckIfPostedUrlIsDownloaded(string url)
        {
            var filenameUrl = url.Split('/').Last();
            return files.CheckIfFileExistsInDB(filenameUrl, true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_saveTimerLock)
                {
                    _disposed = true;
                    _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    _saveTimer.Dispose();
                }
                concurrentConnectionsSemaphore?.Dispose();
                concurrentVideoConnectionsSemaphore?.Dispose();

                foreach (var sw in streamWriters.Values)
                {
                    sw.Dispose();
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
