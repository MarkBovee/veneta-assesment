using Microsoft.EntityFrameworkCore;

namespace Veneta.Assessments.Crawler;

/// <summary>
/// Owns crawler SQLite persistence.
/// </summary>
public sealed class CrawlerDbContext(DbContextOptions<CrawlerDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Gets crawled Consumer rows.
    /// </summary>
    public DbSet<CrawledConsumer> Consumers => Set<CrawledConsumer>();
}
