using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Veneta.Assessments.Backend.ConsumerService.Contracts;

namespace Veneta.Assessments.Backend.ConsumerService.Tests;

/// <summary>
/// Tests Consumer API contracts against actual event and read-model SQLite storage.
/// </summary>
public sealed class ConsumerApiIntegrationTests : IAsyncLifetime
{
    private readonly string _eventStorePath = Path.Combine(Path.GetTempPath(), $"veneta-events-{Guid.NewGuid()}.db");
    private readonly string _readModelPath = Path.Combine(Path.GetTempPath(), $"veneta-read-model-{Guid.NewGuid()}.db");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    /// <summary>
    /// Creates an isolated API host so each test has independent event history.
    /// </summary>
    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:EventStore", $"Data Source={_eventStorePath}");
            builder.UseSetting("ConnectionStrings:ReadModel", $"Data Source={_readModelPath}");
        });
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Proves API creates, projects, updates, and retrieves Consumer through typed HTTP contracts.
    /// </summary>
    [Fact]
    public async Task CreateChangeAndReadAsync_UsesPersistedProjectionAndTypedContracts()
    {
        var created = await CreateAsync("Mark", "Bovee");
        Assert.Equal(1, created.Version);

        var loaded = await _client.GetFromJsonAsync<ConsumerResponse>($"/consumers/{created.Id}");
        Assert.NotNull(loaded);
        Assert.Equal(created, loaded);

        var nameResponse = await _client.PutAsJsonAsync($"/consumers/{created.Id}/name", new ChangeConsumerNameRequest("Markus", "Bovee", 1));
        Assert.Equal(HttpStatusCode.NoContent, nameResponse.StatusCode);
        var addressResponse = await _client.PutAsJsonAsync($"/consumers/{created.Id}/address", new ChangeConsumerAddressRequest(new AddressRequest("Other", "5678CD", "2"), 2));
        Assert.Equal(HttpStatusCode.NoContent, addressResponse.StatusCode);

        var changed = await _client.GetFromJsonAsync<ConsumerResponse>($"/consumers/{created.Id}");
        Assert.NotNull(changed);
        Assert.Equal(3, changed.Version);
        Assert.Equal("Markus", changed.FirstName);
        Assert.Equal(new AddressResponse("Other", "5678CD", "2"), changed.Address);
    }

    /// <summary>
    /// Proves pagination, missing Consumer handling, and stale optimistic-concurrency rejection.
    /// </summary>
    [Fact]
    public async Task QueriesAndStaleWrite_ReturnExpectedContractsAndStatuses()
    {
        var first = await CreateAsync("Mark", "Bovee");
        await CreateAsync("Jane", "Doe");

        var page = await _client.GetFromJsonAsync<ConsumerPageResponse>("/consumers?offset=0&limit=1");
        Assert.NotNull(page);
        Assert.Single(page.Items);
        Assert.Equal(0, page.Offset);
        Assert.Equal(1, page.Limit);
        Assert.Equal(2, page.Total);

        var missing = await _client.GetAsync($"/consumers/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var firstUpdate = await _client.PutAsJsonAsync($"/consumers/{first.Id}/name", new ChangeConsumerNameRequest("Markus", "Bovee", 1));
        Assert.Equal(HttpStatusCode.NoContent, firstUpdate.StatusCode);
        var staleUpdate = await _client.PutAsJsonAsync($"/consumers/{first.Id}/name", new ChangeConsumerNameRequest("Marcus", "Bovee", 1));
        Assert.Equal(HttpStatusCode.Conflict, staleUpdate.StatusCode);
    }

    /// <summary>
    /// Creates Consumer through public API and verifies the created response contract.
    /// </summary>
    /// <param name="firstName">First name to create.</param>
    /// <param name="lastName">Last name to create.</param>
    /// <returns>Typed created Consumer response.</returns>
    private async Task<ConsumerResponse> CreateAsync(string firstName, string lastName)
    {
        var response = await _client.PostAsJsonAsync("/consumers", new CreateConsumerRequest(firstName, lastName, new AddressRequest("Main", "1234AB", "1")));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"firstName\"", json, StringComparison.Ordinal);
        Assert.Contains("\"postalCode\"", json, StringComparison.Ordinal);
        var consumer = JsonSerializer.Deserialize<ConsumerResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var created = Assert.IsType<ConsumerResponse>(consumer);
        Assert.Equal($"/consumers/{created.Id}", response.Headers.Location?.OriginalString);
        return created;
    }

    /// <summary>
    /// Disposes test host and removes temporary SQLite files.
    /// </summary>
    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        File.Delete(_eventStorePath);
        File.Delete(_readModelPath);
        return Task.CompletedTask;
    }
}
