﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using TumblThree.Applications.DataModels;
using TumblThree.Applications.DataModels.TumblrApiJson;
using TumblThree.Applications.DataModels.CrawlerData;
using TumblThree.Applications.DataModels.TumblrPosts;
using TumblThree.Applications.DataModels.TumblrTaggedSearchJson;
using TumblThree.Applications.Downloader;
using TumblThree.Applications.Parser;
using TumblThree.Applications.Properties;
using TumblThree.Applications.Services;
using TumblThree.Domain;
using TumblThree.Domain.Models.Blogs;

namespace TumblThree.Applications.Crawler
{
    [Export(typeof(ICrawler))]
    [ExportMetadata("BlogType", typeof(TumblrTagSearchBlog))]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    public class TumblrTagSearchCrawler : AbstractTumblrCrawler, ICrawler, IDisposable
    {
        private static readonly Regex extractJsonFromSearch = new Regex("window\\['___INITIAL_STATE___'\\] = (.*);");

        private readonly IDownloader downloader;
        private readonly IPostQueue<CrawlerData<Datum>> jsonQueue;
        private readonly IList<string> existingCrawlerData = new List<string>();
        private readonly object existingCrawlerDataLock = new object();

        private SemaphoreSlim semaphoreSlim;
        private List<Task> trackedTasks;

        public TumblrTagSearchCrawler(IShellService shellService, ICrawlerService crawlerService, IWebRequestFactory webRequestFactory,
            ISharedCookieService cookieService, IDownloader downloader, ICrawlerDataDownloader crawlerDataDownloader, ITumblrParser tumblrParser, IImgurParser imgurParser,
            IGfycatParser gfycatParser, IWebmshareParser webmshareParser, IUguuParser uguuParser, ICatBoxParser catboxParser,
            IPostQueue<AbstractPost> postQueue, IPostQueue<CrawlerData<Datum>> jsonQueue, IBlog blog, IProgress<DownloadProgress> progress, PauseToken pt, CancellationToken ct)
            : base(shellService, crawlerService, webRequestFactory, cookieService, tumblrParser, imgurParser, gfycatParser,
                webmshareParser, uguuParser, catboxParser, postQueue, blog, downloader, crawlerDataDownloader, progress, pt, ct)
        {
            this.downloader = downloader;
            this.jsonQueue = jsonQueue;
        }

        public async Task CrawlAsync()
        {
            Logger.Verbose("TumblrTagSearchCrawler.Crawl:Start");

            Task grabber = GetUrlsAsync();
            Task<bool> download = downloader.DownloadBlogAsync();

            Task crawlerDownloader = Task.CompletedTask;
            if (Blog.DumpCrawlerData)
            {
                await GetAlreadyExistingCrawlerDataFilesAsync();
                crawlerDownloader = crawlerDataDownloader.DownloadCrawlerDataAsync();
            }

            await grabber;

            UpdateProgressQueueInformation(Resources.ProgressUniqueDownloads);
            Blog.DuplicatePhotos = DetermineDuplicates<PhotoPost>();
            Blog.DuplicateVideos = DetermineDuplicates<VideoPost>();
            Blog.DuplicateAudios = DetermineDuplicates<AudioPost>();
            Blog.TotalCount = (Blog.TotalCount - Blog.DuplicatePhotos - Blog.DuplicateAudios - Blog.DuplicateVideos);

            CleanCollectedBlogStatistics();

            await crawlerDownloader;
            await download;

            if (!Ct.IsCancellationRequested)
            {
                Blog.LastCompleteCrawl = DateTime.Now;
            }

            Blog.Save();

            UpdateProgressQueueInformation(string.Empty);
        }

        private async Task GetUrlsAsync()
        {
            semaphoreSlim = new SemaphoreSlim(ShellService.Settings.ConcurrentScans);
            trackedTasks = new List<Task>();

            if (!await CheckIfLoggedInAsync())
            {
                Logger.Error("TumblrTagSearchCrawler:GetUrlsAsync: {0}", "User not logged in");
                ShellService.ShowError(new Exception("User not logged in"), Resources.NotLoggedIn, Blog.Name);
                PostQueue.CompleteAdding();
                jsonQueue.CompleteAdding();
                return;
            }

            GenerateTags();

            await semaphoreSlim.WaitAsync();
            trackedTasks.Add(CrawlPageAsync());
            await Task.WhenAll(trackedTasks);

            PostQueue.CompleteAdding();
            jsonQueue.CompleteAdding();

            UpdateBlogStats(true);
        }

        private async Task CrawlPageAsync()
        {
            try
            {
                string document = await GetTaggedSearchPageAsync();
                string json = extractJsonFromSearch.Match(document).Groups[1].Value;
                TagSearch result = ConvertJsonToClass<TagSearch>(json);

                if (result.Tagged.ShouldRedirect)
                {
                    document = await GetTaggedSearchPageAsync(true);
                    json = extractJsonFromSearch.Match(document).Groups[1].Value;
                    result = ConvertJsonToClass<TagSearch>(json);
                }

                string nextUrl = result.ApiUrl + result.Tagged.Timeline.Links.Next.Href;
                string bearerToken = result.ApiFetchStore.APITOKEN;

                DownloadMedia(result);
                while (true)
                {
                    if (CheckIfShouldStop())
                    {
                        return;
                    }

                    CheckIfShouldPause();

                    document = await GetRequestAsync(nextUrl, bearerToken);
                    TumblrTaggedSearchApi apiresult = ConvertJsonToClass<TumblrTaggedSearchApi>(document);
                    if (apiresult.Response.Timeline.Links == null) return;
                    nextUrl = result.ApiUrl + apiresult.Response.Timeline.Links.Next.Href;

                    DownloadMedia(apiresult);
                }
            }
            catch (TimeoutException timeoutException)
            {
                HandleTimeoutException(timeoutException, Resources.Crawling);
            }
            catch (Exception e)
            {
                Logger.Error("CrawlPageAsync: {0}", e);
            }
            finally
            {
                semaphoreSlim.Release();
            }
        }

        protected async Task<string> GetRequestAsync(string url, string bearerToken)
        {
            if (ShellService.Settings.LimitConnectionsSearchApi)
            {
                CrawlerService.TimeconstraintSearchApi.Acquire();
            }
            string[] cookieHosts = { "https://www.tumblr.com/" };
            return await RequestApiDataAsync(url, bearerToken, null, cookieHosts);
        }

        private void DownloadMedia(TumblrTaggedSearchApi page)
        {
            try
            {
                foreach (var post in page.Response.Timeline.Elements)
                {
                    if (post.OriginalType is null ||
                        !CheckIfWithinTimespan(post.Timestamp))
                    {
                        continue;
                    }
                    int index = -1;
                    try
                    {
                        foreach (var content in post.Content)
                        {
                            Post data = new Post()
                            {
                                Date = DateTimeOffset.FromUnixTimeSeconds(post.Timestamp).DateTime.ToString("R"),
                                DateGmt = DateTimeOffset.FromUnixTimeSeconds(post.Timestamp).DateTime.ToString("R"),
                                Type = ConvertContentTypeToPostType(content.Type),
                                Id = post.Id,
                                Tags = new List<string>(post.Tags),
                                Slug = post.Slug,
                                RegularTitle = post.Summary,
                                RebloggedFromName = "",
                                RebloggedRootName = "",
                                ReblogKey = post.ReblogKey,
                                UnixTimestamp = post.Timestamp,
                                Tumblelog = new TumbleLog2(){ Name = post.BlogName }
                            };
                            index += (post.Content.Count > 1) ? 1 : 0;
                            DownloadMedia(content, data, index);
                        }
                        AddToJsonQueue(new CrawlerData<Datum>(Path.ChangeExtension(post.Id, ".json"), post));
                    }
                    catch (TimeoutException timeoutException)
                    {
                        HandleTimeoutException(timeoutException, Resources.Crawling);
                    }
                    catch (Exception ex) //NullReferenceException
                    {
                        Logger.Error("TumblrTagSearchCrawler.DownloadMedia: {0}", ex);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error("DownloadMedia: {0}", e);
            }
        }

        private void DownloadMedia(TagSearch page)
        {
            try
            {
                foreach (var data in page.Tagged.Timeline.Elements)
                {
                    if (!string.Equals(data.ObjectType, "Post", StringComparison.InvariantCultureIgnoreCase)) continue;
                    if (!CheckIfWithinTimespan(data.Timestamp))
                    {
                        continue;
                    }
                    int index = -1;
                    try
                    {
                        foreach (var content in data.Content)
                        {
                            Post post = new Post()
                            {
                                Date = DateTimeOffset.FromUnixTimeSeconds(data.Timestamp).DateTime.ToString("R"),
                                DateGmt = DateTimeOffset.FromUnixTimeSeconds(data.Timestamp).DateTime.ToString("R"),
                                Type = ConvertContentTypeToPostType(content.Type),
                                Id = data.Id,
                                Tags = new List<string>(data.Tags),
                                Slug = data.Slug,
                                RegularTitle = data.Summary,
                                RebloggedFromName = "",
                                RebloggedRootName = "",
                                ReblogKey = data.ReblogKey,
                                UnixTimestamp = data.Timestamp,
                                Tumblelog = new TumbleLog2() { Name = data.BlogName }
                            };
                            index += (data.Content.Count > 1) ? 1 : 0;
                            DownloadMedia(content, post, index);
                        }
                    }
                    catch (TimeoutException timeoutException)
                    {
                        HandleTimeoutException(timeoutException, Resources.Crawling);
                    }
                    catch (NullReferenceException e)
                    {
                        Logger.Verbose("TumblrTagSearchCrawler.DownloadMedia: {0}", e);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error("DownloadMedia: {0}", e);
            }
        }

        private bool CheckIfWithinTimespan(long pagination)
        {
            if (string.IsNullOrEmpty(Blog.DownloadFrom))
            {
                return true;
            }

            DateTime downloadFrom = DateTime.ParseExact(Blog.DownloadFrom, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None);
            var dateTimeOffset = new DateTimeOffset(downloadFrom);
            return pagination >= dateTimeOffset.ToUnixTimeSeconds();
        }

        private void DownloadMedia(Content content, Post post, int index)  //String id, long timestamp, IList<string> tags
        {
            string type = content.Type;
            string url = type == "video" || type == "audio" ? content.Url : content.Media?[0].Url;
            if (url == null)
                return;
            if (!CheckIfContainsTaggedPost(post.Tags))
                return;
            if (CheckIfSkipGif(url))
                return;
            if (type == "video")
            {
                if (Blog.DownloadVideoThumbnail)
                {
                    var thumbnailUrl = content.Poster?[0].Url;
                    AddToDownloadList(new PhotoPost(thumbnailUrl, "", post.Id, post.UnixTimestamp.ToString(), BuildFileName(thumbnailUrl, post, index)));
                }
                // can only download preview image for non-tumblr (embedded) video posts
                if (Blog.DownloadVideo && content.Provider == "tumblr")
                    AddToDownloadList(new VideoPost(url, post.Id, post.UnixTimestamp.ToString(), BuildFileName(url, post, index)));
            }
            else if (type == "audio")
            {
                if (Blog.DownloadAudio && content.Provider == "tumblr")
                    AddToDownloadList(new AudioPost(url, post.Id, post.UnixTimestamp.ToString()));
            }
            else
            {
                if (Blog.DownloadPhoto)
                {
                    var postedUrl = url;
                    if (!Downloader.CheckIfPostedUrlIsDownloaded(url))
                        url = RetrieveOriginalImageUrl(url, 2000, 3000, false);
                    url = CheckPnjUrl(url);
                    AddToDownloadList(new PhotoPost(url, postedUrl, post.Id, post.UnixTimestamp.ToString(), BuildFileName(url, post, index)));
                }
            }
        }

        private bool CheckIfContainsTaggedPost(IList<string> tags)
        {
            return !Tags.Any() || tags.Any(x => Tags.Contains(x, StringComparer.OrdinalIgnoreCase));
        }

        private async Task GetAlreadyExistingCrawlerDataFilesAsync()
        {
            foreach (var filepath in Directory.GetFiles(Blog.DownloadLocation(), "*.json"))
            {
                existingCrawlerData.Add(Path.GetFileName(filepath));
            }
            await Task.CompletedTask;
        }

        private void AddToJsonQueue(CrawlerData<Datum> addToList)
        {
            if (!Blog.DumpCrawlerData) { return; }

            lock (existingCrawlerDataLock)
            {
                if (Blog.ForceRescan || !existingCrawlerData.Contains(addToList.Filename))
                {
                    jsonQueue.Add(addToList);
                    existingCrawlerData.Add(addToList.Filename);
                }
            }
        }

        private async Task<bool> CheckIfLoggedInAsync()
        {
            try
            {
                string document = await GetTaggedSearchPageAsync();
                return document.Contains("API_TOKEN");
            }
            catch (WebException webException) when (webException.Status == WebExceptionStatus.RequestCanceled)
            {
                return true;
            }
            catch (TimeoutException timeoutException)
            {
                HandleTimeoutException(timeoutException, Resources.Crawling);
                return false;
            }
        }

        private async Task<string> GetTaggedSearchPageAsync(bool secondTry = false)
        {
            if (ShellService.Settings.LimitConnectionsSearchApi)
            {
                CrawlerService.TimeconstraintSearchApi.Acquire();
            }

            var url = Blog.Url;
            if (secondTry)
            {
                url = url.Replace("/tagged/", "/tagged/%23");
            }
            return await GetRequestAsync(url);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                semaphoreSlim?.Dispose();
                downloader.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
