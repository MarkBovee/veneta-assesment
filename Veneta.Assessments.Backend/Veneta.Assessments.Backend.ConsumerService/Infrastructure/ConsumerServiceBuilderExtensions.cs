using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NEventStore;
using NEventStore.Persistence.Sql.SqlDialects;
using NEventStore.Serialization.Json;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Veneta.Assessments.Backend.ConsumerService.Application;
using Veneta.Assessments.Backend.ConsumerService.Observability;
using Veneta.Assessments.Backend.ConsumerService.ReadModel;
using Veneta.Assessments.Backend.ConsumerService.Repository;

namespace Veneta.Assessments.Backend.ConsumerService.Infrastructure;

/// <summary>
/// Registers the Consumer API's infrastructure and application services.
/// </summary>
public static class ConsumerServiceBuilderExtensions
{
    /// <summary>
    /// Configures telemetry, event storage, read storage, and application services.
    /// </summary>
    /// <param name="builder">Web application builder.</param>
    public static void AddConsumerService(this WebApplicationBuilder builder)
    {
        // Configure tracing before registering services that emit application activities.
        TelemetryOptions.CapturePayloads = builder.Configuration.GetValue<bool>("Observability:CapturePayloads");
        ConfigureTelemetry(builder);

        var eventStoreConnectionString = GetRequiredConnectionString(builder, "EventStore");
        var readModelConnectionString = GetRequiredConnectionString(builder, "ReadModel");
        var eventStore = BuildEventStore(eventStoreConnectionString);

        // Keep event storage authoritative and the EF model disposable projection state.
        builder.Services.AddSingleton<IStoreEvents>(eventStore);
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(readModelConnectionString));
        builder.Services.AddScoped<ConsumerProjector>();
        builder.Services.AddScoped<ConsumerApplicationService>();
    }

    /// <summary>
    /// Reads a mandatory named connection string from application configuration.
    /// </summary>
    /// <param name="builder">Web application builder.</param>
    /// <param name="name">Connection string name.</param>
    /// <returns>Configured connection string.</returns>
    private static string GetRequiredConnectionString(WebApplicationBuilder builder, string name)
    {
        return builder.Configuration.GetConnectionString(name) ?? throw new InvalidOperationException($"Connection string '{name}' is required.");
    }

    /// <summary>
    /// Builds the authoritative NEventStore instance for Consumer streams.
    /// </summary>
    /// <param name="connectionString">SQLite event store connection string.</param>
    /// <returns>Configured event store.</returns>
    private static IStoreEvents BuildEventStore(string connectionString)
    {
        // Use SQLite persistence with JSON event serialization for local deterministic runs.
        return Wireup.Init().UsingSqlPersistence(SqliteFactory.Instance, connectionString).WithDialect(new MicrosoftDataSqliteDialect()).InitializeStorageEngine().UsingJsonSerialization().Build();
    }

    /// <summary>
    /// Configures ASP.NET Core and domain tracing for the OTLP endpoint.
    /// </summary>
    /// <param name="builder">Web application builder.</param>
    private static void ConfigureTelemetry(WebApplicationBuilder builder)
    {
        // Register application and projection activity sources under the Consumer service resource.
        var telemetry = builder
            .Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("consumer-api"))
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddSource(ConsumerApplicationService.ActivitySourceName).AddSource("Veneta.Assessments.Backend.Projection"));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            // Export only when Aspire supplies a reachable OTLP endpoint.
            telemetry.WithTracing(tracing => tracing.AddOtlpExporter());
        }
    }
}
