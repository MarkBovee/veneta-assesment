using NEventStore;
using Veneta.Assessments.Backend.ConsumerService.ReadModel;
using Veneta.Assessments.Backend.ConsumerService.Repository;

namespace Veneta.Assessments.Backend.ConsumerService.Infrastructure;

/// <summary>
/// Performs the startup work required before the Consumer API accepts requests.
/// </summary>
public static class ConsumerServiceInitializer
{
    /// <summary>
    /// Creates the read model, seeds development data, and rebuilds projections.
    /// </summary>
    /// <param name="app">Running web application.</param>
    /// <param name="readiness">Completion source used by the readiness endpoint.</param>
    public static async Task InitializeConsumerServiceAsync(this WebApplication app, TaskCompletionSource readiness)
    {
        // Resolve the singleton event store before creating the initialization scope.
        var eventStore = app.Services.GetRequiredService<IStoreEvents>();

        // Initialize derived state before advertising readiness to callers.
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.EnsureCreatedAsync();
        await DevelopmentConsumerSeeder.SeedAsync(app.Environment, app.Configuration, eventStore, scope.ServiceProvider, app.Logger);

        // Rebuild projections from committed history so startup can recover disposable state.
        var projector = scope.ServiceProvider.GetRequiredService<ConsumerProjector>();
        var committedEvents = eventStore.Advanced.GetFrom("consumer", 0).ToArray();
        await projector.RebuildAsync(committedEvents, CancellationToken.None);
        readiness.SetResult();
    }
}
