using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Veneta.Assessments.Crawler;

namespace Veneta.Assessments.Crawler.Tests;

/// <summary>
/// Tests crawler pagination and durable idempotent storage.
/// </summary>
public sealed class ConsumerCrawlerTests
{
    /// <summary>
    /// Proves crawler follows every page and persists fields independently from API storage.
    /// </summary>
    [Fact]
    public async Task RunAsync_FetchesAllPagesAndStoresEveryConsumer()
    {
        var first = Consumer(Guid.NewGuid(), 1, "Mark");
        var second = Consumer(Guid.NewGuid(), 1, "Jane");
        var third = Consumer(Guid.NewGuid(), 1, "John");
        using var handler = new StubHandler([Page([first, second], 0, 3), Page([third], 2, 3)]);
        await using var context = CreateContext(out var databasePath);

        try
        {
            await new ConsumerCrawler(new HttpClient(handler) { BaseAddress = new Uri("http://consumer-api") }, context).RunAsync(CancellationToken.None);

            var consumers = await context.Consumers.OrderBy(consumer => consumer.FirstName).ToListAsync();
            Assert.Equal(["Jane", "John", "Mark"], consumers.Select(consumer => consumer.FirstName));
            Assert.Equal(["/consumers?offset=0&limit=100", "/consumers?offset=2&limit=100"], handler.RequestUris);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>
    /// Proves reruns update newer source state and never replace it with a stale page.
    /// </summary>
    [Fact]
    public async Task RunAsync_UpsertsNewerStateAndRejectsOlderState()
    {
        var id = Guid.NewGuid();
        await using var context = CreateContext(out var databasePath);

        try
        {
            await CrawlAsync(context, [Page([Consumer(id, 1, "Mark")], 0, 1)]);
            await CrawlAsync(context, [Page([Consumer(id, 2, "Markus")], 0, 1)]);
            await CrawlAsync(context, [Page([Consumer(id, 1, "Old")], 0, 1)]);

            var consumer = Assert.Single(await context.Consumers.ToListAsync());
            Assert.Equal(2, consumer.Version);
            Assert.Equal("Markus", consumer.FirstName);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>
    /// Proves temporary server failure is retried before crawler persists the successful page.
    /// </summary>
    [Fact]
    public async Task RunAsync_RetriesTransientServerFailure()
    {
        var response = Page([Consumer(Guid.NewGuid(), 1, "Mark")], 0, 1);
        using var handler = new RetryHandler(response);
        await using var context = CreateContext(out var databasePath);

        try
        {
            await new ConsumerCrawler(new HttpClient(handler) { BaseAddress = new Uri("http://consumer-api") }, context).RunAsync(CancellationToken.None);

            Assert.Equal(2, handler.RequestCount);
            Assert.Single(await context.Consumers.ToListAsync());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>
    /// Proves a rate-limit response with expired Retry-After still retries and completes.
    /// </summary>
    [Fact]
    public async Task RunAsync_RetriesRateLimitWithExpiredRetryAfter()
    {
        var response = Page([Consumer(Guid.NewGuid(), 1, "Mark")], 0, 1);
        using var handler = new RetryHandler(response, HttpStatusCode.TooManyRequests, DateTimeOffset.UtcNow.AddSeconds(-1));
        await using var context = CreateContext(out var databasePath);

        try
        {
            await new ConsumerCrawler(new HttpClient(handler) { BaseAddress = new Uri("http://consumer-api") }, context).RunAsync(CancellationToken.None);

            Assert.Equal(2, handler.RequestCount);
            Assert.Single(await context.Consumers.ToListAsync());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>
    /// Proves malformed page progress fails instead of causing an endless one-shot crawl.
    /// </summary>
    [Fact]
    public async Task RunAsync_RejectsEmptyPageBeforeTotalIsReached()
    {
        using var handler = new StubHandler([Page([], 0, 1)]);
        await using var context = CreateContext(out var databasePath);

        try
        {
            var crawler = new ConsumerCrawler(new HttpClient(handler) { BaseAddress = new Uri("http://consumer-api") }, context);
            await Assert.ThrowsAsync<InvalidOperationException>(() => crawler.RunAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>
    /// Proves crawler fails rather than silently accepting duplicate items in one source page.
    /// </summary>
    [Fact]
    public async Task RunAsync_RejectsDuplicateConsumerIdsInPage()
    {
        var consumer = Consumer(Guid.NewGuid(), 1, "Mark");
        using var handler = new StubHandler([Page([consumer, consumer], 0, 2)]);
        await using var context = CreateContext(out var databasePath);

        try
        {
            var crawler = new ConsumerCrawler(new HttpClient(handler) { BaseAddress = new Uri("http://consumer-api") }, context);
            await Assert.ThrowsAsync<InvalidOperationException>(() => crawler.RunAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>
    /// Runs crawler against deterministic in-memory HTTP responses.
    /// </summary>
    /// <param name="context">Crawler database context.</param>
    /// <param name="pages">Responses returned to crawler.</param>
    private static async Task CrawlAsync(CrawlerDbContext context, IReadOnlyList<ConsumerPageResponse> pages)
    {
        using var handler = new StubHandler(pages);
        await new ConsumerCrawler(new HttpClient(handler) { BaseAddress = new Uri("http://consumer-api") }, context).RunAsync(CancellationToken.None);
    }

    /// <summary>
    /// Creates isolated SQLite crawler storage.
    /// </summary>
    /// <param name="databasePath">Receives created database path.</param>
    /// <returns>Ready crawler context.</returns>
    private static CrawlerDbContext CreateContext(out string databasePath)
    {
        databasePath = Path.Combine(Path.GetTempPath(), $"veneta-crawler-{Guid.NewGuid()}.db");
        var context = new CrawlerDbContext(new DbContextOptionsBuilder<CrawlerDbContext>().UseSqlite($"Data Source={databasePath}").Options);
        context.Database.EnsureCreated();
        return context;
    }

    /// <summary>
    /// Creates API Consumer response with distinct data.
    /// </summary>
    /// <param name="id">Consumer identifier.</param>
    /// <param name="version">Source stream version.</param>
    /// <param name="firstName">Consumer first name.</param>
    /// <returns>Consumer API contract.</returns>
    private static ConsumerResponse Consumer(Guid id, int version, string firstName) => new(id, version, firstName, "Bovee", new AddressResponse("Main", "1234AB", "1"));

    /// <summary>
    /// Creates typed API response page.
    /// </summary>
    /// <param name="items">Page items.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="total">Total API items.</param>
    /// <returns>Consumer page response.</returns>
    private static ConsumerPageResponse Page(IReadOnlyList<ConsumerResponse> items, int offset, int total) => new(items, offset, 100, total);

    /// <summary>
    /// Returns queued API pages and captures pagination requests.
    /// </summary>
    private sealed class StubHandler(IReadOnlyList<ConsumerPageResponse> pages) : HttpMessageHandler
    {
        private readonly Queue<ConsumerPageResponse> _pages = new(pages);

        /// <summary>
        /// Gets requested URI paths and queries.
        /// </summary>
        public List<string> RequestUris { get; } = [];

        /// <summary>
        /// Returns next deterministic HTTP response.
        /// </summary>
        /// <param name="request">Received HTTP request.</param>
        /// <param name="cancellationToken">Operation cancellation token.</param>
        /// <returns>HTTP response.</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.PathAndQuery);
            var body = JsonSerializer.Serialize(_pages.Dequeue(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    /// <summary>
    /// Returns one transient server failure followed by a valid page.
    /// </summary>
    /// <param name="page">Successful response page.</param>
    private sealed class RetryHandler(ConsumerPageResponse page, HttpStatusCode transientStatus = HttpStatusCode.InternalServerError, DateTimeOffset? retryAfter = null) : HttpMessageHandler
    {
        /// <summary>
        /// Gets number of requests received.
        /// </summary>
        public int RequestCount { get; private set; }

        /// <summary>
        /// Returns transient failure first, then the successful page.
        /// </summary>
        /// <param name="request">Received HTTP request.</param>
        /// <param name="cancellationToken">Operation cancellation token.</param>
        /// <returns>HTTP response.</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
            {
                var response = new HttpResponseMessage(transientStatus);
                if (retryAfter is not null)
                {
                    response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
                }
                return Task.FromResult(response);
            }
            var body = JsonSerializer.Serialize(page, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
