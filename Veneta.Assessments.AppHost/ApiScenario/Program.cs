using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

const string activitySourceName = "Veneta.Assessments.ApiScenario";
var apiBaseUrl = Environment.GetEnvironmentVariable("API_BASE_URL") ?? throw new InvalidOperationException("API_BASE_URL is required.");
using var loggerFactory = LoggerFactory.Create(logging => logging.AddJsonConsole(options => options.IncludeScopes = true).SetMinimumLevel(LogLevel.Information));
using var telemetry = BuildTelemetry();
using var client = new HttpClient { BaseAddress = new Uri(apiBaseUrl) };
using var scenarioActivity = new ActivitySource(activitySourceName).StartActivity("api.scenario", ActivityKind.Internal);

try
{
    // Correlate every scenario request under one diagnostic scenario id.
    var scenarioId = Guid.NewGuid().ToString("N");
    using var scope = loggerFactory.CreateLogger("ApiScenario").BeginScope(new Dictionary<string, object?> { ["scenario_id"] = scenarioId });
    var created = await RunScenarioAsync(client, loggerFactory.CreateLogger("ApiScenario"), scenarioActivity);
    scenarioActivity?.SetTag("scenario.consumer_id", created.Id);
    scenarioActivity?.SetStatus(ActivityStatusCode.Ok);
    return 0;
}
catch (Exception exception)
{
    // Return a failing process exit code so Aspire reports the scenario failure.
    scenarioActivity?.SetStatus(ActivityStatusCode.Error, exception.Message);
    scenarioActivity?.AddEvent(new ActivityEvent("scenario.failure", tags: new ActivityTagsCollection { ["exception.type"] = exception.GetType().FullName, ["exception.message"] = exception.Message }));
    Console.Error.WriteLine(exception);
    return 1;
}

// Executes the complete public Consumer API workflow against the real Aspire backend.
static async Task<ConsumerResponse> RunScenarioAsync(HttpClient client, ILogger logger, Activity? parentActivity)
{
    // Verify health and pagination before mutating a Consumer.
    await ExpectStatusAsync("health", client.GetAsync("/"), HttpStatusCode.OK, logger, parentActivity);
    var page = await GetAllPagesAsync(client, logger, parentActivity);

    // Create a Consumer and verify it can be read through the public API.
    var created = await PostAsync(client, logger, parentActivity);
    var loaded = await GetAsync<ConsumerResponse>("get-created", client, $"/consumers/{created.Id}", HttpStatusCode.OK, logger, parentActivity);
    Ensure(loaded.Id == created.Id && loaded.Version == 1, "Created Consumer was not readable at version 1.");

    // Apply the supported name and address changes in stream-version order.
    await ExpectStatusAsync(
        "change-name",
        client.PutAsJsonAsync(
            $"/consumers/{created.Id}/name",
            new
            {
                firstName = "Ada-Maria",
                lastName = "Lovelace",
                expectedVersion = 1,
            }
        ),
        HttpStatusCode.NoContent,
        logger,
        parentActivity
    );
    // Verify stale writes, missing resources, and invalid pagination are rejected.
    await ExpectStatusAsync(
        "change-address",
        client.PutAsJsonAsync(
            $"/consumers/{created.Id}/address",
            new
            {
                address = new
                {
                    street = "Analytical Engine Lane",
                    postalCode = "1000ZZ",
                    houseNumber = "42",
                },
                expectedVersion = 2,
            }
        ),
        HttpStatusCode.NoContent,
        logger,
        parentActivity
    );

    var changed = await GetAsync<ConsumerResponse>("get-changed", client, $"/consumers/{created.Id}", HttpStatusCode.OK, logger, parentActivity);
    Ensure(changed.Version == 3 && changed.FirstName == "Ada-Maria" && changed.Address.PostalCode == "1000ZZ", "Updated Consumer state was not projected correctly.");

    await ExpectStatusAsync(
        "stale-concurrency",
        client.PutAsJsonAsync(
            $"/consumers/{created.Id}/name",
            new
            {
                firstName = "Stale",
                lastName = "Writer",
                expectedVersion = 1,
            }
        ),
        HttpStatusCode.Conflict,
        logger,
        parentActivity
    );
    await ExpectStatusAsync("missing-consumer", client.GetAsync($"/consumers/{Guid.NewGuid()}"), HttpStatusCode.NotFound, logger, parentActivity);
    await ExpectStatusAsync("invalid-pagination", client.GetAsync("/consumers?offset=-1&limit=100"), HttpStatusCode.BadRequest, logger, parentActivity);

    logger.LogInformation("API scenario completed successfully for Consumer {ConsumerId} at version {Version}.", changed.Id, changed.Version);
    return changed;
}

// Follows every API page so the scenario demonstrates real pagination behavior.
static async Task<ConsumerPageResponse> GetAllPagesAsync(HttpClient client, ILogger logger, Activity? parentActivity)
{
    const int pageSize = 100;
    var offset = 0;
    ConsumerPageResponse? firstPage = null;
    var pageCount = 0;

    do
    {
        // Follow the server-provided total until every page has been consumed.
        var page = await GetAsync<ConsumerPageResponse>($"list-page-{pageCount + 1}", client, $"/consumers?offset={offset}&limit={pageSize}", HttpStatusCode.OK, logger, parentActivity);
        firstPage ??= page;
        pageCount++;
        offset += page.Items.Count;
        Ensure(offset >= page.Total || page.Items.Count > 0, "Pagination did not make progress.");
        logger.LogInformation("Read API page {PageNumber}: offset={Offset}, items={ItemCount}, total={Total}.", pageCount, page.Offset, page.Items.Count, page.Total);
    } while (firstPage is not null && offset < firstPage.Total);

    return firstPage ?? throw new InvalidOperationException("API returned no page.");
}

// Creates a Consumer and validates the full response contract.
static async Task<ConsumerResponse> PostAsync(HttpClient client, ILogger logger, Activity? parentActivity)
{
    // Create one representative Consumer for the scenario workflow.
    using var activity = StartStep("create-consumer", parentActivity);
    // Send the request using the public JSON contract.
    var response = await client.PostAsJsonAsync(
        "/consumers",
        new
        {
            firstName = "Ada",
            lastName = "Lovelace",
            address = new
            {
                street = "Analytical Engine Lane",
                postalCode = "1000AA",
                houseNumber = "1",
            },
        }
    );
    activity?.SetTag("http.response.status_code", (int)response.StatusCode);
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<ConsumerResponse>() ?? throw new InvalidOperationException("Create response was empty.");
    activity?.SetTag("consumer.id", result.Id);
    activity?.SetTag("consumer.version", result.Version);
    activity?.SetStatus(ActivityStatusCode.Ok);
    logger.LogInformation("Created scenario Consumer {ConsumerId}.", result.Id);
    return result;
}

// Executes a typed GET and records endpoint details in a child span.
static async Task<T> GetAsync<T>(string stepName, HttpClient client, string path, HttpStatusCode expectedStatus, ILogger logger, Activity? parentActivity)
{
    // Execute a typed GET and validate its status before deserializing the body.
    using var activity = StartStep(stepName, parentActivity);
    var response = await client.GetAsync(path);
    activity?.SetTag("http.route", path);
    activity?.SetTag("http.response.status_code", (int)response.StatusCode);
    Ensure(response.StatusCode == expectedStatus, $"{stepName}: expected {(int)expectedStatus}, got {(int)response.StatusCode}.");
    logger.LogInformation("API step {StepName} returned HTTP {StatusCode}.", stepName, (int)response.StatusCode);
    var result = await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidOperationException($"{stepName} returned an empty response.");
    response.Dispose();
    activity?.SetStatus(ActivityStatusCode.Ok);
    return result;
}

// Executes a request and verifies its expected API status.
static async Task ExpectStatusAsync(string stepName, Task<HttpResponseMessage> responseTask, HttpStatusCode expectedStatus, ILogger logger, Activity? parentActivity)
{
    // Validate status-only requests while keeping the response lifetime bounded.
    using var activity = StartStep(stepName, parentActivity);
    using var response = await responseTask;
    activity?.SetTag("http.response.status_code", (int)response.StatusCode);
    activity?.SetTag("http.expected_status_code", (int)expectedStatus);
    if (response.StatusCode != expectedStatus)
    {
        activity?.SetStatus(ActivityStatusCode.Error, $"Expected {(int)expectedStatus}, got {(int)response.StatusCode}.");
        throw new InvalidOperationException($"{stepName}: expected {(int)expectedStatus}, got {(int)response.StatusCode}.");
    }

    activity?.SetStatus(ActivityStatusCode.Ok);
    logger.LogInformation("API step {StepName} returned HTTP {StatusCode}.", stepName, (int)response.StatusCode);
}

// Creates a child span for one named scenario step.
static Activity? StartStep(string stepName, Activity? parentActivity)
{
    // Create a child span so each scenario operation is independently searchable.
    var source = new ActivitySource("Veneta.Assessments.ApiScenario");
    var activity = source.StartActivity($"scenario.{stepName}", ActivityKind.Internal);
    activity?.SetTag("scenario.step", stepName);
    return activity;
}

// Validates an invariant and fails the scenario with a useful diagnostic.
static void Ensure(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

// Configures OTLP traces when Aspire supplies an exporter endpoint.
static TracerProvider? BuildTelemetry()
{
    // Keep local runs dependency-free when Aspire does not provide an OTLP endpoint.
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
    {
        return null;
    }

    return Sdk.CreateTracerProviderBuilder().SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("api-scenario")).AddSource("Veneta.Assessments.ApiScenario").AddHttpClientInstrumentation().AddOtlpExporter().Build();
}

// Represents the API response for one Consumer.
public sealed record ConsumerResponse(Guid Id, int Version, string FirstName, string LastName, AddressResponse Address);

// Represents the API response for an address.
public sealed record AddressResponse(string Street, string PostalCode, string HouseNumber);

// Represents the API response for a paged Consumer query.
public sealed record ConsumerPageResponse(IReadOnlyList<ConsumerResponse> Items, int Offset, int Limit, int Total);
