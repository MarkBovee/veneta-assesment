using NEventStore;
using Veneta.Assessments.Backend.ConsumerService.Application;
using Veneta.Assessments.Backend.ConsumerService.Domain;

namespace Veneta.Assessments.Backend.ConsumerService.Infrastructure;

/// <summary>
/// Adds deterministic development Consumers without changing production data.
/// </summary>
public static class DevelopmentConsumerSeeder
{
    /// <summary>
    /// Adds missing development Consumers until the configured target is reached.
    /// </summary>
    /// <param name="environment">Current hosting environment.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="eventStore">Authoritative event store.</param>
    /// <param name="services">Scoped service provider.</param>
    /// <param name="logger">Application logger.</param>
    public static async Task SeedAsync(IHostEnvironment environment, IConfiguration configuration, IStoreEvents eventStore, IServiceProvider services, ILogger logger)
    {
        // Never create demo data outside the local Development environment.
        if (!environment.IsDevelopment())
        {
            return;
        }

        // Compare the configured target with authoritative event-store streams.
        var seedCount = GetSeedCount(configuration);
        var existingStreamIds = eventStore.Advanced.GetFrom("consumer", 0).Select(commit => commit.StreamId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingSeedCount = Math.Max(0, seedCount - existingStreamIds.Count);
        if (missingSeedCount == 0)
        {
            return;
        }

        var applicationService = services.GetRequiredService<ConsumerApplicationService>();
        var seeds = CreateSeeds(seedCount).Where(consumer => !existingStreamIds.Contains(consumer.Id.ToString())).Take(missingSeedCount).ToArray();

        // Use the normal application service so seeded data follows production event flow.
        foreach (var consumer in seeds)
        {
            await applicationService.CreateAsync(consumer, CancellationToken.None);
        }

        logger.LogInformation("Development seed added {ConsumerCount} consumers; target={TargetCount}, existing={ExistingCount}.", seeds.Length, seedCount, existingStreamIds.Count);
    }

    /// <summary>
    /// Reads and bounds the configured development seed count.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>Seed target between zero and ten thousand.</returns>
    private static int GetSeedCount(IConfiguration configuration)
    {
        return int.TryParse(configuration["DevelopmentSeed:ConsumerCount"], out var configuredSeedCount) ? Math.Clamp(configuredSeedCount, 0, 10000) : 250;
    }

    /// <summary>
    /// Builds stable demo data large enough to exercise multiple API pages.
    /// </summary>
    /// <param name="seedCount">Number of Consumers to create.</param>
    /// <returns>Deterministic seed aggregates.</returns>
    private static Consumer[] CreateSeeds(int seedCount)
    {
        // Keep a few recognizable names before generating the larger deterministic range.
        var seeds = new List<Consumer>(seedCount);
        var namedSeeds = new (string FirstName, string LastName, string Street, string PostalCode, string HouseNumber)[]
        {
            ("Ada", "Lovelace", "Analytical Engine Lane", "1000AA", "1"),
            ("Alan", "Turing", "Computing Street", "2000BB", "2"),
            ("Grace", "Hopper", "Compiler Avenue", "3000CC", "3"),
        };

        foreach (var seed in namedSeeds.Take(seedCount))
        {
            seeds.Add(Consumer.Create(Guid.NewGuid(), seed.FirstName, seed.LastName, new ConsumerAddress(seed.Street, seed.PostalCode, seed.HouseNumber)));
        }

        for (var index = seeds.Count; index < seedCount; index++)
        {
            var number = index + 1;
            seeds.Add(Consumer.Create(Guid.NewGuid(), $"Demo{number:000}", "Consumer", new ConsumerAddress($"Demo Street {number}", $"{1000 + number:0000}AA", number.ToString())));
        }

        return seeds.ToArray();
    }
}
