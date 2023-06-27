﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TumblThree.Applications.DataModels;
using TumblThree.Applications.DataModels.CrawlerData;
using TumblThree.Applications.DataModels.TumblrPosts;
using TumblThree.Applications.DataModels.TumblrSvcJson;
using TumblThree.Applications.Downloader;
using TumblThree.Applications.Parser;
using TumblThree.Applications.Properties;
using TumblThree.Applications.Services;
using TumblThree.Domain;
using TumblThree.Domain.Models.Blogs;

namespace TumblThree.Applications.Crawler
{
    [Export(typeof(ICrawler))]
    [ExportMetadata("BlogType", typeof(TumblrHiddenBlog))]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    public class TumblrHiddenCrawler : AbstractTumblrCrawler, ICrawler, IDisposable
    {
        private readonly IDownloader downloader;
        private readonly ITumblrToTextParser<Post> tumblrJsonParser;
        private readonly IPostQueue<CrawlerData<Post>> jsonQueue;
        private readonly IEnvironmentService environmentService;
        private readonly ILoginService loginService;
        private readonly IList<string> existingCrawlerData = new List<string>();
        private readonly object existingCrawlerDataLock = new object();

        private string tumblrKey = string.Empty;

        private bool incompleteCrawl;

        private SemaphoreSlim semaphoreSlim;
        private List<Task> trackedTasks;

        private int numberOfPagesCrawled;

        public TumblrHiddenCrawler(IShellService shellService, ICrawlerService crawlerService, IWebRequestFactory webRequestFactory,
            ISharedCookieService cookieService, IDownloader downloader, ICrawlerDataDownloader crawlerDataDownloader,
            ITumblrToTextParser<Post> tumblrJsonParser, ITumblrParser tumblrParser, IImgurParser imgurParser,
            IGfycatParser gfycatParser, IWebmshareParser webmshareParser, IUguuParser uguuParser, ICatBoxParser catboxParser,
            IPostQueue<AbstractPost> postQueue, IPostQueue<CrawlerData<Post>> jsonQueue, IBlog blog, IProgress<DownloadProgress> progress, PauseToken pt, CancellationToken ct,
            IEnvironmentService environmentService, ILoginService loginService)
            : base(shellService, crawlerService, webRequestFactory, cookieService, tumblrParser, imgurParser, gfycatParser,
                webmshareParser, uguuParser, catboxParser, postQueue, blog, downloader, crawlerDataDownloader, progress, pt,
                ct)
        {
            this.downloader = downloader;
            this.tumblrJsonParser = tumblrJsonParser;
            this.jsonQueue = jsonQueue;
            this.environmentService = environmentService;
            this.loginService = loginService;
        }

        public override async Task IsBlogOnlineAsync()
        {
            if (!await CheckIfLoggedInAsync())
            {
                Logger.Error("TumblrHiddenCrawler:IsBlogOnlineAsync: {0}", "User not logged in");
                ShellService.ShowError(new Exception("User not logged in"), Resources.NotLoggedIn, Blog.Name);
                PostQueue.CompleteAdding();
                jsonQueue.CompleteAdding();
                return;
            }

            try
            {
                tumblrKey = await UpdateTumblrKeyAsync("https://www.tumblr.com/dashboard/blog/" + Blog.Name);
                string document = await GetSvcPageAsync("1", "0");
                Blog.Online = true;
            }
            catch (WebException webException)
            {
                if (webException.Status == WebExceptionStatus.RequestCanceled)
                {
                    return;
                }

                if (HandleServiceUnavailableWebException(webException))
                {
                    Blog.Online = true;
                }
                else if (HandleNotFoundWebException(webException))
                {
                    Blog.Online = false;
                }
                else if (HandleLimitExceededWebException(webException))
                {
                    Blog.Online = true;
                }
            }
            catch (TimeoutException timeoutException)
            {
                HandleTimeoutException(timeoutException, Resources.OnlineChecking);
                Blog.Online = false;
            }
            catch (Exception ex) when (ex.Message == "Acceptance of privacy consent needed!")
            {
                Blog.Online = false;
            }
        }

        public override async Task UpdateMetaInformationAsync()
        {
            if (!Blog.Online)
            {
                return;
            }

            try
            {
                tumblrKey = await UpdateTumblrKeyAsync("https://www.tumblr.com/dashboard/blog/" + Blog.Name);
                string document = await GetSvcPageAsync("1", "0");
                var response = ConvertJsonToClass<TumblrJson>(document);

                if (response.Meta.Status == 200)
                {
                    Blog.Title = response.Response.Posts.FirstOrDefault().Blog.Title;
                    Blog.Description = response.Response.Posts.FirstOrDefault().Blog.Description;
                }
            }
            catch (WebException webException)
            {
                if (webException.Status == WebExceptionStatus.RequestCanceled)
                {
                    return;
                }

                HandleServiceUnavailableWebException(webException);
            }
        }

        public async Task CrawlAsync()
        {
            Logger.Verbose("TumblrHiddenCrawler.Crawl:Start");

            ulong highestId = await GetHighestPostIdAsync();
            Task<bool> grabber = GetUrlsAsync();
            Task<bool> download = downloader.DownloadBlogAsync();

            Task crawlerDownloader = Task.CompletedTask;
            if (Blog.DumpCrawlerData)
            {
                await GetAlreadyExistingCrawlerDataFilesAsync();
                crawlerDownloader = crawlerDataDownloader.DownloadCrawlerDataAsync();
            }

            bool apiLimitHit = await grabber;

            UpdateProgressQueueInformation(Resources.ProgressUniqueDownloads);
            UpdateBlogDuplicates();

            CleanCollectedBlogStatistics();

            await crawlerDownloader;
            bool finishedDownloading = await download;

            if (!Ct.IsCancellationRequested)
            {
                Blog.LastCompleteCrawl = DateTime.Now;
                if (finishedDownloading && !apiLimitHit)
                {
                    Blog.LastId = highestId;
                }
            }

            Blog.Save();

            UpdateProgressQueueInformation(string.Empty);
        }

        protected override IEnumerable<int> GetPageNumbers()
        {
            if (!TestRange(Blog.PageSize, 1, 100))
            {
                Blog.PageSize = 100;
            }

            return string.IsNullOrEmpty(Blog.DownloadPages)
                ? Enumerable.Range(0, ShellService.Settings.ConcurrentScans)
                : RangeToSequence(Blog.DownloadPages);
        }

        private async Task<bool> GetUrlsAsync()
        {
            semaphoreSlim = new SemaphoreSlim(ShellService.Settings.ConcurrentScans);
            trackedTasks = new List<Task>();

            GenerateTags();

            foreach (int pageNumber in GetPageNumbers())
            {
                await semaphoreSlim.WaitAsync();
                trackedTasks.Add(CrawlPageAsync(pageNumber));
            }

            await Task.WhenAll(trackedTasks);

            PostQueue.CompleteAdding();
            jsonQueue.CompleteAdding();

            UpdateBlogStats(GetLastPostId() != 0);

            return incompleteCrawl;
        }

        private new bool HandleUnauthorizedWebException(WebException webException)
        {
            var resp = (HttpWebResponse)webException?.Response;
            if (resp == null || resp.StatusCode != HttpStatusCode.Unauthorized)
            {
                return false;
            }

            Logger.Error("Auth error: {0}", webException.Message);
            ShellService.ShowError(webException, "Auth error (trying to continue): {0}", webException.Message);
            return true;
        }

        private void LogCookies(HttpWebRequest request, string type)
        {
            Logger.Verbose("Cookies {0}", type);
            Logger.Verbose(" main");
            foreach (Cookie cookie in request.CookieContainer.GetCookies(new Uri("https://www.tumblr.com/")))
            {
                Logger.Verbose("  {0}: {1}", cookie.Name, cookie.Value);
            }
            Logger.Verbose(" blog");
            foreach (Cookie cookie in request.CookieContainer.GetCookies(new Uri("https://" + Blog.Name.Replace("+", "-") + ".tumblr.com")))
            {
                Logger.Verbose("  {0}: {1}", cookie.Name, cookie.Value);
            }
        }

        private async Task CrawlPageAsync(int pageNumber)
        {
            try
            {
                string document = null;
                try
                {
                    document = await GetSvcPageAsync(Blog.PageSize.ToString(), (Blog.PageSize * pageNumber).ToString());
                }
                catch (WebException webEx)
                {
                    if (HandleUnauthorizedWebException(webEx))
                    {
                        await FetchCookiesAgainAsync();
                        document = await GetSvcPageAsync(Blog.PageSize.ToString(), (Blog.PageSize * pageNumber).ToString());
                    }
                    else
                    {
                        throw;
                    }
                }
                var response = ConvertJsonToClass<TumblrJson>(document);
                await AddUrlsToDownloadListAsync(response, pageNumber);
            }
            catch (WebException webException)
            {
                if (HandleLimitExceededWebException(webException) ||
                    HandleUnauthorizedWebException(webException))
                {
                    incompleteCrawl = true;
                }
            }
            catch (TimeoutException timeoutException)
            {
                incompleteCrawl = true;
                HandleTimeoutException(timeoutException, Resources.Crawling);
            }
            catch (Exception ex)
            {
                Logger.Error("TumblrHiddenCrawler.CrawlPageAsync: {0}", ex);
            }
            finally
            {
                semaphoreSlim.Release();
            }
        }

        private static CookieCollection GetCookies(List<CoreWebView2Cookie> cookies)
        {
            CookieCollection cookieCollection = new CookieCollection();
            foreach (var cookie in cookies)
            {
                var transferCookie = new System.Net.Cookie(cookie.Name, WebUtility.UrlEncode(cookie.Value), cookie.Path, cookie.Domain);
                transferCookie.Expires = cookie.Expires;
                transferCookie.HttpOnly = cookie.IsHttpOnly;
                transferCookie.Secure = cookie.IsSecure;
                cookieCollection.Add(transferCookie);
            }
            return cookieCollection;
        }

        private async Task<bool> FetchCookiesAgainAsync()
        {
            var appSettingsPath = Path.GetFullPath(Path.Combine(environmentService.AppSettingsPath, ".."));
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, appSettingsPath);
            using (WebView2 browser = new WebView2())
            {
                await browser.EnsureCoreWebView2Async(env);
                var cookieManager = browser.CoreWebView2.CookieManager;
                var cookies = await cookieManager.GetCookiesAsync("https://www.tumblr.com/");
                CookieCollection cookieCollection = GetCookies(cookies);
                loginService.AddCookies(cookieCollection);
            }
            return true;
        }

        private async Task<ulong> GetHighestPostIdAsync()
        {
            ulong lastId = Blog.LastId;
            try
            {
                return await GetHighestPostIdCoreAsync();
            }
            catch (WebException webException)
            {
                if (webException.Status == WebExceptionStatus.RequestCanceled)
                {
                    return lastId;
                }

                HandleLimitExceededWebException(webException);
                if (HandleUnauthorizedWebException(webException))
                {
                    await FetchCookiesAgainAsync();
                }
                return lastId;
            }
            catch (TimeoutException timeoutException)
            {
                HandleTimeoutException(timeoutException, Resources.Crawling);
                return lastId;
            }
        }

        private async Task<ulong> GetHighestPostIdCoreAsync()
        {
            string document = await GetSvcPageAsync("1", "0");
            var response = ConvertJsonToClass<TumblrJson>(document);

            Post post = response.Response?.Posts?.FirstOrDefault(x => !x.IsPinned);
            if (DateTime.TryParse(post?.Date, out var latestPost)) Blog.LatestPost = latestPost;
            _ = ulong.TryParse(post?.Id, out var highestId);
            return highestId;
        }

        private bool PostWithinTimeSpan(Post post)
        {
            if (string.IsNullOrEmpty(Blog.DownloadFrom) && string.IsNullOrEmpty(Blog.DownloadTo))
            {
                return true;
            }

            long downloadFromUnixTime = 0;
            long downloadToUnixTime = long.MaxValue;
            if (!string.IsNullOrEmpty(Blog.DownloadFrom))
            {
                DateTime downloadFrom = DateTime.ParseExact(Blog.DownloadFrom, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None);
                downloadFromUnixTime = new DateTimeOffset(downloadFrom).ToUnixTimeSeconds();
            }

            if (!string.IsNullOrEmpty(Blog.DownloadTo))
            {
                DateTime downloadTo = DateTime.ParseExact(Blog.DownloadTo, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None).AddDays(1);
                downloadToUnixTime = new DateTimeOffset(downloadTo).ToUnixTimeSeconds();
            }

            long postTime = Convert.ToInt64(post.Timestamp);
            return downloadFromUnixTime <= postTime && postTime < downloadToUnixTime;
        }

        private async Task<bool> CheckIfLoggedInAsync()
        {
            try
            {
                string document = await GetSvcPageAsync(Blog.PageSize.ToString(), (Blog.PageSize * 1).ToString());
            }
            catch (WebException webException)
            {
                if (webException.Status == WebExceptionStatus.RequestCanceled)
                {
                    return true;
                }

                if (HandleServiceUnavailableWebException(webException))
                {
                    return false;
                }
            }
            catch (TimeoutException timeoutException)
            {
                HandleTimeoutException(timeoutException, Resources.Crawling);
                return false;
            }

            return true;
        }

        private async Task<string> GetSvcPageAsync(string limit, string offset)
        {
            if (ShellService.Settings.LimitConnectionsSvc)
            {
                CrawlerService.TimeconstraintSvc.Acquire();
            }

            return await RequestDataAsync(limit, offset);
        }

        protected virtual async Task<string> RequestDataAsync(string limit, string offset)
        {
            var requestRegistration = new CancellationTokenRegistration();
            HttpWebRequest request = null;
            try
            {
                string url = @"https://www.tumblr.com/svc/indash_blog?tumblelog_name_or_id=" + Blog.Name +
                             @"&post_id=&limit=" + limit + "&offset=" + offset + "&should_bypass_safemode=true";
                string referer = @"https://www.tumblr.com/dashboard/blog/" + Blog.Name;
                var headers = new Dictionary<string, string> { { "X-tumblr-form-key", tumblrKey } };
                request = WebRequestFactory.CreateGetXhrRequest(url, referer, headers);
                CookieService.GetUriCookie(request.CookieContainer, new Uri("https://www.tumblr.com/"));
                CookieService.GetUriCookie(request.CookieContainer, new Uri("https://" + Blog.Name.Replace("+", "-") + ".tumblr.com"));
                requestRegistration = Ct.Register(() => request.Abort());
                LogCookies(request, "request");
                string response = await WebRequestFactory.ReadRequestToEndAsync(request, true);
                return response;
            }
            catch (WebException ex)
            {
                var resp = (HttpWebResponse)ex?.Response;
                if (resp?.StatusCode == HttpStatusCode.Unauthorized)
                {
                    LogCookies(request, "error");
                }
                throw;
            }
            finally
            {
                requestRegistration.Dispose();
            }
        }

        private async Task AddUrlsToDownloadListAsync(TumblrJson response, int crawlerNumber)
        {
            while (true)
            {
                if (CheckIfShouldStop()) { return; }

                CheckIfShouldPause();

                if (!CheckPostAge(response)) { return; }

                var lastPostId = GetLastPostId();
                foreach (Post post in response.Response.Posts)
                {
                    try
                    {
                        if (CheckIfShouldStop()) { break; }
                        CheckIfShouldPause();
                        if (lastPostId > 0 && ulong.TryParse(post.Id, out var postId) && postId < lastPostId) { continue; }
                        if (!PostWithinTimeSpan(post)) { continue; }
                        if (!CheckIfContainsTaggedPost(post)) { continue; }
                        if (!CheckIfDownloadRebloggedPosts(post)) { continue; }

                        try
                        {
                            AddPhotoUrlToDownloadList(post);
                            AddVideoUrlToDownloadList(post);
                            AddAudioUrlToDownloadList(post);
                            AddTextUrlToDownloadList(post);
                            AddQuoteUrlToDownloadList(post);
                            AddLinkUrlToDownloadList(post);
                            AddConversationUrlToDownloadList(post);
                            AddAnswerUrlToDownloadList(post);
                            AddPhotoMetaUrlToDownloadList(post);
                            AddVideoMetaUrlToDownloadList(post);
                            AddAudioMetaUrlToDownloadList(post);
                            await AddExternalPhotoUrlToDownloadListAsync(post);
                        }
                        catch (NullReferenceException e)
                        {
                            Logger.Verbose("TumblrHiddenCrawler.AddUrlsToDownloadListAsync: {0}", e);
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Error("TumblrHiddenCrawler.AddUrlsToDownloadListAsync: {0}", e);
                        ShellService.ShowError(e, "{0}: Error parsing post!", Blog.Name);
                    }
                }

                Interlocked.Increment(ref numberOfPagesCrawled);
                UpdateProgressQueueInformation(Resources.ProgressGetUrlShort, numberOfPagesCrawled);

                string document = await GetSvcPageAsync(Blog.PageSize.ToString(), (Blog.PageSize * crawlerNumber).ToString());
                response = ConvertJsonToClass<TumblrJson>(document);
                if (!response.Response.Posts.Any() || !string.IsNullOrEmpty(Blog.DownloadPages)) { return; }

                crawlerNumber += ShellService.Settings.ConcurrentScans;
            }
        }

        private bool CheckPostAge(TumblrJson document)
        {
            ulong highestPostId = 0;
            var post = document.Response.Posts.FirstOrDefault(x => !x.IsPinned);
            if (post == null) return false;
            _ = ulong.TryParse(post.Id, out highestPostId);
            return highestPostId >= GetLastPostId();
        }

        private bool CheckIfDownloadRebloggedPosts(Post post)
        {
            return Blog.DownloadRebloggedPosts || string.IsNullOrEmpty(post.RebloggedFromName) || post.RebloggedFromName == Blog.Name;
        }

        private async Task GetAlreadyExistingCrawlerDataFilesAsync()
        {
            foreach (var filepath in Directory.GetFiles(Blog.DownloadLocation(), "*.json"))
            {
                existingCrawlerData.Add(Path.GetFileName(filepath));
            }
            await Task.CompletedTask;
        }

        private void AddToJsonQueue(CrawlerData<Post> addToList)
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

        private void AddToJsonQueue(string[] urls, Post post)
        {
            if (urls == null || urls.Length == 0) return;
            AddToJsonQueue(new CrawlerData<Post>(Path.ChangeExtension(FileName(urls[0]), ".json"), post));
        }

        private bool CheckIfContainsTaggedPost(Post post)
        {
            return !Tags.Any() || post.Tags.Any(x => Tags.Contains(x, StringComparer.OrdinalIgnoreCase));
        }

        private void AddPhotoUrlToDownloadList(Post post)
        {
            if (!Blog.DownloadPhoto) { return; }

            Post postCopy = post;
            if (post.Type == "photo")
            {
                AddPhotoUrl(post);
                postCopy = (Post)post.Clone();
                postCopy.Photos.Clear();
            }

            AddInlinePhotoUrl(postCopy);

            if (Blog.RegExPhotos)
            {
                AddGenericInlinePhotoUrl(post);
            }
        }

        private void AddPhotoUrl(Post post)
        {
            string postId = post.Id;
            bool jsonSaved = false;
            int i = 1;
            if (post.Photos.Count != 0 && post.Photos[0].AltSizes.FirstOrDefault().Url.Split('/').Last().StartsWith("tumblr_")) i = -1;
            foreach (Photo photo in post.Photos)
            {
                string imageUrl = photo.AltSizes.Where(url => url.Width == int.Parse(ImageSizeForSearching())).Select(url => url.Url)
                                       .FirstOrDefault() ??
                                  photo.AltSizes.FirstOrDefault().Url;

                if (ShellService.Settings.ImageSize == "best")
                {
                    imageUrl = photo.AltSizes.FirstOrDefault().Url;
                }

                if (CheckIfSkipGif(imageUrl)) { continue; }
                imageUrl = CheckPnjUrl(imageUrl);

                var filename = BuildFileName(imageUrl, post, i);
                AddDownloadedMedia(imageUrl, filename, post);
                AddToDownloadList(new PhotoPost(imageUrl, "", postId, post.Timestamp.ToString(), filename));
                if (!jsonSaved || !Blog.GroupPhotoSets && !(string.Equals(Blog.FilenameTemplate, "%f", StringComparison.OrdinalIgnoreCase) && i == -1))
                {
                    jsonSaved = true;
                    AddToJsonQueue(new CrawlerData<Post>(Path.ChangeExtension(imageUrl.Split('/').Last(), ".json"), post));
                }
                if (i != -1) i++;
            }
        }

        private void AddInlinePhotoUrl(Post post)
        {
            AddTumblrPhotoUrl(InlineSearch(post), ConvertTumblrApiJson(post));
        }

        private void AddGenericInlinePhotoUrl(Post post)
        {
            AddTumblrPhotoUrl(InlineSearch(post), ConvertTumblrApiJson(post));
        }

        private void AddVideoUrlToDownloadList(Post post)
        {
            if (!Blog.DownloadVideo && !Blog.DownloadVideoThumbnail) { return; }

            Post postCopy = post;
            if (post.Type == "video")
            {
                AddVideoUrl(post);

                postCopy = (Post)post.Clone();
                postCopy.VideoUrl = string.Empty;
            }

            var urls = AddTumblrVideoUrl(InlineSearch(postCopy), ConvertTumblrApiJson(post));
            AddToJsonQueue(urls, post);
            urls = AddInlineTumblrVideoUrl(InlineSearch(postCopy), ConvertTumblrApiJson(post));
            AddToJsonQueue(urls, post);

            if (Blog.DownloadVideo && Blog.RegExVideos)
            {
                AddGenericInlineVideoUrl(postCopy);
            }
        }

        private void AddVideoUrl(Post post)
        {
            if (post.VideoUrl == null) { return; }

            string postId = post.Id;
            string videoUrl = post.VideoUrl;

            if (ShellService.Settings.VideoSize == 480)
            {
                if (!videoUrl.Contains("_480"))
                {
                    videoUrl = videoUrl.Replace(".mp4", "_480.mp4");
                }
            }

            if (Blog.DownloadVideo)
            {
                var filename = BuildFileName(videoUrl, post, -1);
                AddDownloadedMedia(videoUrl, filename, post);
                AddToDownloadList(new VideoPost(videoUrl, postId, post.Timestamp.ToString(), filename));
                AddToJsonQueue(new CrawlerData<Post>(Path.ChangeExtension(videoUrl.Split('/').Last(), ".json"), post));
            }

            if (Blog.DownloadVideoThumbnail)
            {
                var filename = BuildFileName(post.ThumbnailUrl, post, "photo", -1);
                AddDownloadedMedia(post.ThumbnailUrl, filename, post);
                AddToDownloadList(new PhotoPost(post.ThumbnailUrl, "", postId, post.Timestamp.ToString(), filename));
                if (!Blog.DownloadVideo)
                {
                    AddToJsonQueue(new CrawlerData<Post>(Path.ChangeExtension(videoUrl.Split('/').Last(), ".json"), post));
                }
            }
        }

        private void AddGenericInlineVideoUrl(Post post)
        {
            AddGenericVideoUrl(InlineSearch(post), ConvertTumblrApiJson(post));
        }

        private void AddAudioUrlToDownloadList(Post post)
        {
            if (!Blog.DownloadAudio) { return; }
            if (post.Type != "audio") { return; }

            string postId = post.Id;
            string audioUrl = post.AudioUrl;
            if (!audioUrl.EndsWith(".mp3"))
            {
                audioUrl = audioUrl + ".mp3";
            }

            var filename = BuildFileName(audioUrl, post, -1);
            AddDownloadedMedia(audioUrl, filename, post);
            AddToDownloadList(new AudioPost(audioUrl, postId, post.Timestamp.ToString(), filename));
            AddToJsonQueue(new CrawlerData<Post>(Path.ChangeExtension(audioUrl.Split('/').Last(), ".json"), post));
        }

        private void AddTextUrlToDownloadList(Post post)
        {
            if (!Blog.DownloadText) { return; }
            if (post.Type != "text") { return; }

            string postId = post.Id;
            string textBody = tumblrJsonParser.ParseText(post);
            AddToDownloadList(new TextPost(textBody, postId, post.Timestamp.ToString()));
            AddToJsonQueue(new CrawlerData<Post>(Path.ChangeExtension(postId, ".json"), post));
        }

        private void AddQuoteUrlToDownloadList(Post post)
        {
            if (!Blog.DownloadQuote) { return; }
            if (post.Type != "quote") { return; }

            string postId = post.Id;
            string textBody = tumblrJsonParser.ParseQuote(post);
            AddToDownloadList(new QuotePost(textBody, postId, post.Timestamp.ToString()));
            AddToJsonQueue(new CrawlerData<Post>(Path.ChangeExtension(postId, ".json"), post));
        }

        private void AddLinkUrlToDownloadList(Post post)
        {
            if (!Blog.DownloadLink) { return; }
            if (post.Type != "link") { return; }

            string postId = post.Id;
            string textBody = tumblrJsonParser.ParseLink(post);
            AddToDownloadList(new LinkPost(textBody, postId, post.Timestamp.ToString()));
            AddToJsonQueue(new CrawlerData<Post>(Path.ChangeExtension(postId, ".json"), post));
        }

        private void AddConversationUrlToDownloadList(Post post)
        {
            if (!Blog.DownloadConversation) { return; }
            if (post.Type != "chat") { return; }

            string postId = post.Id;
            string textBody = tumblrJsonParser.ParseConversation(post);
            AddToDownloadList(new ConversationPost(textBody, postId, post.Timestamp.ToString()));
            AddToJsonQueue(new CrawlerData<Post>(Path.ChangeExtension(postId, ".json"), post));
        }

        private void AddAnswerUrlToDownloadList(Post post)
        {
            if (!Blog.DownloadAnswer) { return; }
            if (post.Type != "answer") { return; }

            string postId = post.Id;
            string textBody = tumblrJsonParser.ParseAnswer(post);
            AddToDownloadList(new AnswerPost(textBody, postId, post.Timestamp.ToString()));
            AddToJsonQueue(new CrawlerData<Post>(Path.ChangeExtension(postId, ".json"), post));
        }

        private void AddPhotoMetaUrlToDownloadList(Post post)
        {
            if (!Blog.CreatePhotoMeta) { return; }
            if (post.Type != "photo") { return; }

            string postId = post.Id;
            string textBody = tumblrJsonParser.ParsePhotoMeta(post);
            AddToDownloadList(new PhotoMetaPost(textBody, postId));
            AddToJsonQueue(new CrawlerData<Post>(Path.ChangeExtension(postId, ".json"), post));
        }

        private void AddVideoMetaUrlToDownloadList(Post post)
        {
            if (!Blog.CreateVideoMeta) { return; }
            if (post.Type != "video") { return; }

            string postId = post.Id;
            string textBody = tumblrJsonParser.ParseVideoMeta(post);
            AddToDownloadList(new VideoMetaPost(textBody, postId));
            AddToJsonQueue(new CrawlerData<Post>(Path.ChangeExtension(postId, ".json"), post));
        }

        private void AddAudioMetaUrlToDownloadList(Post post)
        {
            if (!Blog.CreateAudioMeta) { return; }
            if (post.Type != "audio") { return; }

            string postId = post.Id;
            string textBody = tumblrJsonParser.ParseAudioMeta(post);
            AddToDownloadList(new AudioMetaPost(textBody, postId));
            AddToJsonQueue(new CrawlerData<Post>(Path.ChangeExtension(postId, ".json"), post));
        }

        private static string InlineSearch(Post post)
        {
            return string.Join(" ", post.Trail?.Select(trail => trail.ContentRaw) ?? Enumerable.Empty<string>());
        }

        private async Task AddExternalPhotoUrlToDownloadListAsync(Post post)
        {
            string searchableText = InlineSearch(post);
            string timestamp = post.Timestamp.ToString();

            if (Blog.DownloadImgur)
            {
                AddImgurUrl(searchableText, timestamp);
                await AddImgurAlbumUrlAsync(searchableText, timestamp);
            }

            if (Blog.DownloadGfycat)
            {
                await AddGfycatUrlAsync(searchableText, timestamp);
            }

            if (Blog.DownloadWebmshare)
            {
                AddWebmshareUrl(searchableText, timestamp);
            }

            if (Blog.DownloadUguu)
            {
                AddUguuUrl(searchableText, timestamp);
            }

            if (Blog.DownloadCatBox)
            {
                AddCatBoxUrl(searchableText, timestamp);
            }
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
