using HtmlAgilityPack;
using KamiYomu.CrawlerAgents.Core;
using KamiYomu.CrawlerAgents.Core.Catalog;
using KamiYomu.CrawlerAgents.Core.Catalog.Builders;
using KamiYomu.CrawlerAgents.Core.Catalog.Definitions;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Page = KamiYomu.CrawlerAgents.Core.Catalog.Page;

namespace KamiYomu.CrawlerAgents.MangaKatana
{
    [DisplayName("KamiYomu Crawler Agent – mangakatana.com")]
    public class MangaKatanaCrawlerAgent : AbstractCrawlerAgent, ICrawlerAgent, IAsyncDisposable
    {
        private bool _disposed = false;
        private Lazy<HttpClient> _httpClient;
        private Lazy<Task<IBrowser>> _browser;
        
        public MangaKatanaCrawlerAgent(IDictionary<string, object> options) : base(options)
        {
            _httpClient = new Lazy<HttpClient>(CreateHttpClient, true);
            _browser = new Lazy<Task<IBrowser>>(CreateBrowserAsync, true);
        }
        public Task<IBrowser> GetBrowserAsync() => _browser.Value;

        private async Task<IBrowser> CreateBrowserAsync()
        {
            var launchOptions = new LaunchOptions
            {
                Headless = true,
                Timeout = TimeoutMilliseconds,
                Args = ["--no-sandbox", "--disable-setuid-sandbox"]
            };

            return await Puppeteer.LaunchAsync(launchOptions);
        }


        private HttpClient CreateHttpClient()
        {
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://mangakatana.com"),
                Timeout = TimeSpan.FromMilliseconds(TimeoutMilliseconds)
            };

            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(HttpClientDefaultUserAgent);
            return httpClient;
        }

        /// <inheritdoc/>
        public async Task<Manga> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            var browser = await GetBrowserAsync();
            using var page = await browser.NewPageAsync();
            await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

            var finalUrl = new Uri(_httpClient.Value.BaseAddress, $"manga/{id}").ToString();
            var response = await page.GoToAsync(finalUrl, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.Networkidle0],
                Timeout = TimeoutMilliseconds
            });

            var content = await response.TextAsync();
            var document = new HtmlDocument();
            document.LoadHtml(content);
            var rootNode = document.DocumentNode.SelectSingleNode("//*[@id='single_book']");
            Manga manga = ConvertToMangaFromSingleBook(rootNode, id);

            return manga;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<Page>> GetChapterPagesAsync(Chapter chapter, CancellationToken cancellationToken = default)
        {
            var browser = await GetBrowserAsync();
            using var page = await browser.NewPageAsync();
            await page.SetUserAgentAsync(HttpClientDefaultUserAgent);
            // Wait for full page load including JS execution
            var response = await page.GoToAsync(chapter.Uri.ToString(), new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.Networkidle0],
                Timeout = TimeoutMilliseconds
            });

            var content = await page.GetContentAsync();
            var document = new HtmlDocument();
            document.LoadHtml(content);

            var pageNodes = document.DocumentNode.SelectNodes("//div[@id='imgs']//div[contains(@class, 'wrap_img')]");
            return ConvertToChapterPages(chapter, pageNodes);
        }

        /// <inheritdoc/>
        public async Task<PagedResult<Manga>> SearchAsync(string titleName, PaginationOptions paginationOptions, CancellationToken cancellationToken)
        {
            var browser = await GetBrowserAsync();
            using var page = await browser.NewPageAsync();
            await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

            var queryParams = new Dictionary<string, string>
            {
                ["search"] = titleName,
                ["search_by"] = "book_name"
            };
            var encodedQuery = new FormUrlEncodedContent(queryParams).ReadAsStringAsync(cancellationToken).Result;
            var builder = new UriBuilder(_httpClient.Value.BaseAddress)
            {
                Query = encodedQuery
            };

            var finalUrl = builder.Uri.ToString();

            var response = await page.GoToAsync(finalUrl, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.Networkidle0],
                Timeout = TimeoutMilliseconds
            });

            var content = await response.TextAsync();
            var document = new HtmlDocument();
            document.LoadHtml(content);

            List<Manga> mangas = [];
            HtmlNodeCollection nodes = document.DocumentNode.SelectNodes("//*[@id='book_list']/div[contains(@class, 'item')]");
            foreach (var divNode in nodes)
            {
                Manga manga = ConvertToMangaFromList(divNode);
                mangas.Add(manga);
            }

            return PagedResultBuilder<Manga>.Create()
                .WithData(mangas)
                .WithPaginationOptions(new PaginationOptions(mangas.Count(), mangas.Count(), mangas.Count()))
                .Build();
        }

        /// <inheritdoc/>
        public async Task<PagedResult<Chapter>> GetChaptersAsync(Manga manga, PaginationOptions paginationOptions, CancellationToken cancellationToken)
        {
            var browser = await GetBrowserAsync();
            using var page = await browser.NewPageAsync();
            await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

            var finalUrl = new Uri(_httpClient.Value.BaseAddress, $"manga/{manga.Id}").ToString();
            var response = await page.GoToAsync(finalUrl, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.Networkidle0],
                Timeout = TimeoutMilliseconds
            });

            var content = await response.TextAsync();
            var document = new HtmlDocument();
            document.LoadHtml(content);
            var rootNode = document.DocumentNode.SelectSingleNode("//*[@id='single_book']");
            IEnumerable<Chapter> chapters = ConvertChaptersFromSingleBook(manga, rootNode);

            return PagedResultBuilder<Chapter>.Create()
                                              .WithPaginationOptions(new PaginationOptions(chapters.Count(), chapters.Count(), chapters.Count()))
                                              .WithData(chapters)
                                              .Build();
        }


        /// <inheritdoc/>
        public Task<Uri> GetFaviconAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new Uri("https://mangakatana.com/static/img/fav.png"));
        }


        private IEnumerable<Page> ConvertToChapterPages(Chapter chapter, HtmlNodeCollection pageNodes)
        {
            if (pageNodes == null)
                return [];

            var pages = new List<Page>();

            foreach (var node in pageNodes)
            {
                var idAttr = node.GetAttributeValue("id", "");
                if (!idAttr.StartsWith("page")) continue;

                if (!decimal.TryParse(idAttr.Substring(4), out decimal pageNumber))
                    continue;

                var imgNode = node.SelectSingleNode(".//img");
                if (imgNode == null) continue;

                var imageUrl = imgNode.GetAttributeValue("data-src", null)
                              ?? imgNode.GetAttributeValue("src", null);

                if (string.IsNullOrEmpty(imageUrl)) continue;

                var page = PageBuilder.Create()
                                      .WithChapterId(chapter.Id)
                                      .WithId(idAttr) 
                                      .WithPageNumber(pageNumber)
                                      .WithImageUrl(new Uri(imageUrl))
                                      .WithParentChapter(chapter)
                                      .Build();

                pages.Add(page);
            }

            return pages;
        }

        private static Manga ConvertToMangaFromList(HtmlNode divNode)
        {
            var mangaBuilder = MangaBuilder.Create();

            // URL and Title
            var titleNode = divNode.SelectSingleNode(".//h3[@class='title']/a");
            var url = titleNode?.GetAttributeValue("href", string.Empty);
            var title = string.IsNullOrEmpty(titleNode?.InnerText.Trim()) ? "Untitled Manga" : titleNode?.InnerText.Trim();
            var id = url?.Split('/').Last();
            // Cover image
            var coverNode = divNode.SelectSingleNode(".//div[@class='wrap_img']//img");
            var coverUrl = coverNode?.GetAttributeValue("src", string.Empty);

            // Status
            var statusNode = divNode.SelectSingleNode(".//div[contains(@class, 'status')]");
            var status = statusNode?.InnerText.Trim();

            // Release date
            var releaseDateNode = divNode.SelectSingleNode(".//div[@class='uk-width-1-2']/div[contains(@class, 'date')]");
            var releaseDate = releaseDateNode?.InnerText.Trim();

            // First chapter link
            var firstChapterNode = divNode.SelectSingleNode(".//div[@class='uk-text-right']//a");
            var firstChapterUrl = firstChapterNode?.GetAttributeValue("href", string.Empty);

            // Genres
            var genreNodes = divNode.SelectNodes(".//div[contains(@class, 'genres')]//a");
            var genres = genreNodes?.Select(g => g.InnerText.Trim()).ToList() ?? [];

            // Summary
            var summaryNode = divNode.SelectSingleNode(".//div[contains(@class, 'summary')]");
            var summary = summaryNode?.InnerText.Trim();

            // Build Manga object
            mangaBuilder
                .WithId(id)
                .WithTitle(title)
                .WithDescription(summary)
                .WithWebsiteUrl(url)
                .WithCoverFileName(Path.GetFileName(coverUrl))
                .WithCoverUrl(new Uri(coverUrl))
                .WithTags([.. genres])
                .WithReleaseStatus(status.ToLower() switch
                {
                    "completed" => ReleaseStatus.Completed,
                    "ongoing" => ReleaseStatus.Continuing,
                    _ => ReleaseStatus.Unreleased
                })
                .WithYear(DateTime.TryParseExact(releaseDate, "MMM-dd-yyyy", null, System.Globalization.DateTimeStyles.None, out var releaseDateTime) ? releaseDateTime.Year : 0)
                .WithIsFamilySafe(true);
            return mangaBuilder.Build();
        }

        private static Manga ConvertToMangaFromSingleBook(HtmlNode rootNode, string id)
        {
            var mangaBuilder = MangaBuilder.Create();

            // Title and URL
            var titleNode = rootNode.SelectSingleNode(".//h1[@class='heading']");
            var title = string.IsNullOrEmpty(titleNode?.InnerText.Trim()) ? "Untitled Manga" : titleNode?.InnerText.Trim();

            var url = rootNode.SelectSingleNode(".//a[contains(@class, 'fc_bt')]")
                              ?.GetAttributeValue("href", string.Empty);

            // Cover image
            var coverUrl = rootNode.SelectSingleNode(".//div[@class='cover']//img")
                                   ?.GetAttributeValue("src", string.Empty);

            // Status
            var status = rootNode.SelectSingleNode(".//div[contains(@class, 'status')]")
                                 ?.InnerText.Trim();

            // Release date
            var releaseDate = rootNode.SelectSingleNode(".//div[contains(@class, 'updateAt')]")
                                      ?.InnerText.Trim();

            // First chapter URL
            var firstChapterUrl = rootNode.SelectSingleNode(".//a[contains(@class, 'fc_bt')]")
                                          ?.GetAttributeValue("href", string.Empty);

            // Genres
            var genreNodes = rootNode.SelectNodes(".//div[@class='genres']//a");
            var genres = genreNodes?.Select(g => g.InnerText.Trim()).ToList() ?? [];

            // Summary
            var summary = rootNode.SelectSingleNode(".//div[@class='summary']/p")
                                  ?.InnerText.Trim();

            var latestChapterNode = rootNode.SelectSingleNode("//li[div[@class='d-cell-small label' and contains(text(), 'Latest chapter(s):')]]//div[@class='new_chap']");

            var latestChapter = latestChapterNode?.InnerText.Trim();

            var match = Regex.Match(latestChapter ?? "", @"([\d\.]+)");
            var chapterNumber = match.Success ? match.Groups[1].Value : "0";

            var latestChapterDecimal = decimal.TryParse(chapterNumber, out var result) ? result : 0m;

            // Build Manga object
            mangaBuilder
                .WithId(id)
                .WithTitle(title)
                .WithDescription(summary)
                .WithWebsiteUrl(url)
                .WithCoverFileName(Path.GetFileName(coverUrl))
                .WithCoverUrl(new Uri(coverUrl))
                .WithTags([.. genres])
                .WithLatestChapterAvailable(latestChapterDecimal)
                .WithReleaseStatus(status?.ToLower() switch
                {
                    "completed" => ReleaseStatus.Completed,
                    "ongoing" => ReleaseStatus.Continuing,
                    _ => ReleaseStatus.Unreleased
                })
                .WithYear(DateTime.TryParseExact(releaseDate, "MMM-dd-yyyy", null, System.Globalization.DateTimeStyles.None, out var releaseDateTime) ? releaseDateTime.Year : 0)
                .WithIsFamilySafe(true);

            return mangaBuilder.Build();
        }

        private static IEnumerable<Chapter> ConvertChaptersFromSingleBook(Manga manga, HtmlNode rootNode)
        {
            var chapterRows = rootNode.SelectNodes(".//div[@class='chapters']//tr");
            var chapters = new List<Chapter>();

            if (chapterRows == null)
                return chapters;

            foreach (var row in chapterRows)
            {
                var chapterLink = row.SelectSingleNode(".//div[@class='chapter']/a");
                var updateTime = row.SelectSingleNode(".//div[@class='update_time']");

                if (chapterLink == null)
                    continue;

                var title = string.IsNullOrEmpty(chapterLink?.InnerText.Trim()) ? "Untitled Chapter" : chapterLink?.InnerText.Trim(); 
                var uri = chapterLink.GetAttributeValue("href", string.Empty);
                var chapterId = uri.Split('/').Last();
                var updatedAt = updateTime?.InnerText.Trim();

                var match = Regex.Match(title ?? "", @"Chapter\s+([\d\.]+)");
                var number = match.Success && decimal.TryParse(match.Groups[1].Value, out var result) ? result : 0m;
                var chapterBuilder = ChapterBuilder.Create();

                chapterBuilder
                    .WithId(chapterId)
                    .WithTitle(title)
                    .WithParentManga(manga)
                    .WithVolume(0)
                    .WithNumber(number)
                    .WithUri(new Uri(uri));

                chapters.Add(chapterBuilder.Build());
            }

            return chapters;
        }
        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                if (_httpClient.IsValueCreated)
                {
                    _httpClient.Value.Dispose();
                }

                if (_browser.IsValueCreated)
                {
                    var browserTask = _browser.Value;
                    if (browserTask.IsCompletedSuccessfully)
                    {
                        browserTask.Result.Dispose();
                    }
                }
            }

            _disposed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            if (_httpClient.IsValueCreated)
            {
                _httpClient.Value.Dispose();
            }

            if (_browser.IsValueCreated)
            {
                try
                {
                    var browser = await _browser.Value;
                    await browser.CloseAsync();
                    await browser.DisposeAsync();
                }
                catch(Exception ex)
                {
                    Logger?.LogError("{crawler}, Error disposing browser: {Message}", nameof(MangaKatanaCrawlerAgent), ex.Message);
                }
            }

            _disposed = true;
        }


        ~MangaKatanaCrawlerAgent()
        {
            Dispose(false);
        }
    }
}
