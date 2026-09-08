using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Veneta.Assessments.Crawler;

/// <summary>
/// Fetches every Consumer API page and upserts it into crawler-owned storage.
/// </summary>
public sealed class ConsumerCrawler
{
    private const int MaxAttempts = 4;
    private const int RetryBaseDelayMilliseconds = 250;
    private const int RetryJitterMilliseconds = 100;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    public const string MeterName = "Veneta.Assessments.Crawler";
    public const string ActivitySourceName = "Veneta.Assessments.Crawler.Tracing";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> CrawlCounter = Meter.CreateCounter<long>("veneta.crawler.runs", unit: "{run}");
    private static readonly Histogram<double> CrawlDuration = Meter.CreateHistogram<double>("veneta.crawler.duration", unit: "s");
    private static readonly Counter<long> PageCounter = Meter.CreateCounter<long>("veneta.crawler.pages", unit: "{page}");
    private static readonly Counter<long> RetryCounter = Meter.CreateCounter<long>("veneta.crawler.retries", unit: "{retry}");
    private static readonly Counter<long> UpsertCounter = Meter.CreateCounter<long>("veneta.crawler.upserts", unit: "{row}");

    private readonly HttpClient _client;
    private readonly CrawlerDbContext _context;
    private readonly ILogger<ConsumerCrawler> _logger;
    private readonly int _pageSize;

    /// <summary>
    /// Initializes the one-shot crawler.
    /// </summary>
    /// <param name="client">HTTP client targeting Consumer API.</param>
    /// <param name="context">Crawler-owned database context.</param>
    /// <param name="logger">Structured crawler logger.</param>
    /// <param name="pageSize">Number of source records requested per API page.</param>
    public ConsumerCrawler(HttpClient client, CrawlerDbContext context, ILogger<ConsumerCrawler>? logger = null, int pageSize = 100)
    {
        _client = client;
        _context = context;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConsumerCrawler>.Instance;
        _pageSize = pageSize is >= 1 and <= 100 ? pageSize : throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 100.");
    }

    /// <summary>
    /// Fetches all pages, persists them, and returns when the crawl is complete.
    /// </summary>
    /// <param name="cancellationToken">Operation cancellation token.</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Start one trace and metric lifetime for the complete one-shot crawl.
        using var activity = ActivitySource.StartActivity("crawler.run", ActivityKind.Internal);
        var startedAt = Stopwatch.GetTimestamp();
        var crawlId = Guid.NewGuid().ToString("N");
        using var scope = _logger.BeginScope(new Dictionary<string, object?> { ["crawl_id"] = crawlId });
        var offset = 0;
        int? total = null;
        var pageCount = 0;
        var itemCount = 0;

        _logger.LogInformation("Crawler started for {ApiBaseUrl}.", _client.BaseAddress);

        try
        {
            // Continue following pages until the API-reported total has been consumed.
            bool hasMorePages;
            do
            {
                // Fetch the next page before validating and persisting its contents.
                var page = await GetPageAsync(offset, _pageSize, cancellationToken);
                total ??= page.Total;

                // Reject inconsistent or duplicate pagination before mutating local storage.
                var hasDuplicateIds = page.Items.Select(consumer => consumer.Id).Distinct().Count() != page.Items.Count;
                var hasInvalidPagination = page.Total != total || page.Offset != offset || page.Limit != _pageSize || page.Items.Count > _pageSize || page.Items.Count > page.Total - offset || page.Items.Count == 0 && offset < page.Total;

                if (hasDuplicateIds || hasInvalidPagination)
                {
                    throw new InvalidOperationException("Consumer API returned an invalid pagination response.");
                }

                // Upsert the validated page while protecting newer local versions.
                var upsertSummary = await UpsertPageAsync(page.Items, cancellationToken);
                pageCount++;
                itemCount += page.Items.Count;
                offset += page.Items.Count;
                hasMorePages = offset < total;
                PageCounter.Add(1);
                _logger.LogInformation(
                    "Crawler page processed: offset={Offset}, items={ItemCount}, total={Total}, inserted={Inserted}, updated={Updated}, staleSkipped={StaleSkipped}.",
                    page.Offset,
                    page.Items.Count,
                    total,
                    upsertSummary.Inserted,
                    upsertSummary.Updated,
                    upsertSummary.StaleSkipped
                );
            } while (hasMorePages);

            CrawlCounter.Add(1, new KeyValuePair<string, object?>("result", "success"));
            activity?.SetTag("crawler.pages", pageCount);
            activity?.SetTag("crawler.items", itemCount);
            activity?.SetStatus(ActivityStatusCode.Ok);
            _logger.LogInformation("Crawler completed: pages={PageCount}, items={ItemCount}, durationMs={DurationMs}.", pageCount, itemCount, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
        catch (Exception exception)
        {
            // Record failure telemetry and rethrow so the caller observes the failed crawl.
            CrawlCounter.Add(1, new KeyValuePair<string, object?>("result", "error"));
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection { ["exception.type"] = exception.GetType().FullName, ["exception.message"] = exception.Message }));
            _logger.LogError(exception, "Crawler failed at offset={Offset}.", offset);
            throw;
        }
        finally
        {
            // Record elapsed time for both successful and failed crawls.
            CrawlDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalSeconds);
        }
    }

    /// <summary>
    /// Persists one page, retaining newer local state if source data is stale.
    /// </summary>
    /// <param name="consumers">Consumers received in API page.</param>
    /// <param name="cancellationToken">Operation cancellation token.</param>
    private async Task<UpsertSummary> UpsertPageAsync(IEnumerable<ConsumerResponse> consumers, CancellationToken cancellationToken)
    {
        // Load existing rows in one query so version comparisons stay page-local.
        using var activity = ActivitySource.StartActivity("crawler.upsert_page", ActivityKind.Internal);
        var sourceConsumers = consumers.ToList();
        activity?.SetTag("crawler.page.items", sourceConsumers.Count);
        var sourceIds = sourceConsumers.Select(consumer => consumer.Id).ToArray();
        var storedConsumers = await _context.Consumers.Where(consumer => sourceIds.Contains(consumer.Id)).ToDictionaryAsync(consumer => consumer.Id, cancellationToken);
        var inserted = 0;
        var updated = 0;
        var staleSkipped = 0;

        foreach (var consumer in sourceConsumers)
        {
            if (!storedConsumers.TryGetValue(consumer.Id, out var storedConsumer))
            {
                // Insert consumers that have not been seen by the crawler before.
                var newConsumer = new CrawledConsumer
                {
                    Id = consumer.Id,
                    Version = consumer.Version,
                    FirstName = consumer.FirstName,
                    LastName = consumer.LastName,
                    Street = consumer.Address.Street,
                    PostalCode = consumer.Address.PostalCode,
                    HouseNumber = consumer.Address.HouseNumber,
                };
                _context.Consumers.Add(newConsumer);
                storedConsumers.Add(consumer.Id, newConsumer);
                inserted++;
                continue;
            }

            if (storedConsumer.Version > consumer.Version)
            {
                // Never overwrite newer local state with stale source data.
                staleSkipped++;
                continue;
            }
            storedConsumer.Version = consumer.Version;
            storedConsumer.FirstName = consumer.FirstName;
            storedConsumer.LastName = consumer.LastName;
            storedConsumer.Street = consumer.Address.Street;
            storedConsumer.PostalCode = consumer.Address.PostalCode;
            storedConsumer.HouseNumber = consumer.Address.HouseNumber;
            updated++;
        }

        // Persist all inserts and updates together after the page has been reconciled.
        await _context.SaveChangesAsync(cancellationToken);
        UpsertCounter.Add(inserted, new KeyValuePair<string, object?>("operation", "insert"));
        UpsertCounter.Add(updated, new KeyValuePair<string, object?>("operation", "update"));
        UpsertCounter.Add(staleSkipped, new KeyValuePair<string, object?>("operation", "stale_skip"));
        activity?.SetTag("crawler.inserted", inserted);
        activity?.SetTag("crawler.updated", updated);
        activity?.SetTag("crawler.stale_skipped", staleSkipped);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return new UpsertSummary(inserted, updated, staleSkipped);
    }

    /// <summary>
    /// Loads one Consumer API page with bounded transient retries.
    /// </summary>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Operation cancellation token.</param>
    /// <returns>Deserialized Consumer page.</returns>
    private async Task<ConsumerPageResponse> GetPageAsync(int offset, int limit, CancellationToken cancellationToken)
    {
        // Trace each page request independently so retries remain observable.
        using var activity = ActivitySource.StartActivity("crawler.fetch_page", ActivityKind.Internal);
        activity?.SetTag("crawler.offset", offset);
        activity?.SetTag("crawler.limit", limit);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                // Build the bounded API request from the current crawl position.
                var requestUri = $"/consumers?offset={offset}&limit={limit}";
                using var response = await _client.GetAsync(requestUri, cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    // Honor transient server throttling before trying the request again.
                    if (attempt == MaxAttempts - 1)
                    {
                        response.EnsureSuccessStatusCode();
                    }
                    var retryAfter = GetRetryDelay(response, attempt);
                    RetryCounter.Add(1, new KeyValuePair<string, object?>("reason", "http_status"));
                    _logger.LogWarning("Crawler request retry: offset={Offset}, attempt={Attempt}, statusCode={StatusCode}, delayMs={DelayMs}.", offset, attempt + 1, (int)response.StatusCode, retryAfter.TotalMilliseconds);
                    await Task.Delay(retryAfter, cancellationToken);
                    continue;
                }

                // Deserialize only successful responses and reject empty payloads.
                response.EnsureSuccessStatusCode();
                var page = await response.Content.ReadFromJsonAsync<ConsumerPageResponse>(cancellationToken) ?? throw new InvalidOperationException("Backend returned an empty response.");
                activity?.SetTag("crawler.attempts", attempt + 1);
                activity?.SetTag("http.response.status_code", (int)response.StatusCode);
                activity?.SetTag("crawler.response.items", page.Items.Count);
                activity?.SetTag("crawler.response.total", page.Total);
                activity?.SetStatus(ActivityStatusCode.Ok);
                _logger.LogDebug("Crawler page fetched: offset={Offset}, attempt={Attempt}, statusCode={StatusCode}.", offset, attempt + 1, (int)response.StatusCode);
                return page;
            }
            catch (HttpRequestException) when (attempt < MaxAttempts - 1)
            {
                // Retry network failures with exponential backoff and jitter.
                var retryDelay = CalculateRetryDelay(attempt);
                RetryCounter.Add(1, new KeyValuePair<string, object?>("reason", "network"));
                _logger.LogWarning("Crawler network retry: offset={Offset}, attempt={Attempt}, delayMs={DelayMs}.", offset, attempt + 1, retryDelay.TotalMilliseconds);
                await Task.Delay(retryDelay, cancellationToken);
            }
        }

        throw new InvalidOperationException("Crawler request retries were exhausted.");
    }

    /// <summary>
    /// Returns a bounded retry delay, honoring valid Retry-After responses.
    /// </summary>
    /// <param name="response">Transient failure response.</param>
    /// <param name="attempt">Zero-based retry attempt.</param>
    /// <returns>Non-negative delay capped at one minute.</returns>
    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        // Prefer the server-provided delay but retain a bounded local fallback.
        var fallback = CalculateRetryDelay(attempt);
        var retryAfter = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow ?? fallback;

        if (retryAfter <= TimeSpan.Zero)
        {
            return fallback;
        }

        return retryAfter > MaxRetryDelay ? MaxRetryDelay : retryAfter;
    }

    /// <summary>
    /// Calculates exponential backoff with bounded jitter for transient failures.
    /// </summary>
    private static TimeSpan CalculateRetryDelay(int attempt) => TimeSpan.FromMilliseconds(RetryBaseDelayMilliseconds * Math.Pow(2, attempt) + Random.Shared.Next(RetryJitterMilliseconds));

    /// <summary>
    /// Groups the outcomes of one crawler page upsert.
    /// </summary>
    private readonly record struct UpsertSummary(int Inserted, int Updated, int StaleSkipped);
}
