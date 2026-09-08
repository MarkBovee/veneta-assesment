using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Veneta.Assessments.Crawler;

// Load crawler settings from local configuration and Aspire environment variables.
var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
var apiBaseUrl = configuration["Crawler:ApiBaseUrl"] ?? throw new InvalidOperationException("Crawler:ApiBaseUrl is required.");
var configuredDatabasePath = configuration["Crawler:DatabasePath"] ?? "Data/crawler.sqlite";
var pageSize = int.TryParse(configuration["Crawler:PageSize"], out var configuredPageSize) ? configuredPageSize : 100;
var dataRoot = configuration["VENETA_DATA_DIR"] ?? FindRepositoryRoot(Directory.GetCurrentDirectory());
var databasePath = Path.IsPathRooted(configuredDatabasePath) ? configuredDatabasePath : Path.GetFullPath(configuredDatabasePath, dataRoot);
var options = new DbContextOptionsBuilder<CrawlerDbContext>().UseSqlite($"Data Source={databasePath}").Options;
using var loggerFactory = LoggerFactory.Create(logging => logging.AddJsonConsole(options => options.IncludeScopes = true).SetMinimumLevel(LogLevel.Information));
using var telemetry = BuildTelemetry(configuration);
await using var context = new CrawlerDbContext(options);

// Create crawler-owned storage before the one-shot crawl begins.
await context.Database.EnsureCreatedAsync();
using var client = new HttpClient { BaseAddress = new Uri(apiBaseUrl) };

// Run one bounded crawl and exit when all API pages have been processed.
await new ConsumerCrawler(client, context, loggerFactory.CreateLogger<ConsumerCrawler>(), pageSize).RunAsync(CancellationToken.None);

// Exports crawler metrics and traces to the Aspire dashboard when OTLP is configured.
static IDisposable? BuildTelemetry(IConfiguration configuration)
{
    // Only create exporters when the hosting environment provides an OTLP endpoint.
    if (string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    {
        return null;
    }

    var resource = ResourceBuilder.CreateDefault().AddService("consumer-crawler");
    var meterProvider = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(resource).AddMeter(ConsumerCrawler.MeterName).AddOtlpExporter().Build();
    var tracerProvider = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource).AddSource(ConsumerCrawler.ActivitySourceName).AddHttpClientInstrumentation().AddOtlpExporter().Build();

    return new TelemetryLifetime(meterProvider, tracerProvider);
}

// Finds the repository root when Aspire or Rider starts the crawler below it.
static string FindRepositoryRoot(string startDirectory)
{
    // Walk upward so relative database paths resolve from the repository root.
    var directory = new DirectoryInfo(startDirectory);
    while (directory is not null)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, "Data")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return startDirectory;
}
