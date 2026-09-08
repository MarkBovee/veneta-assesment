using Microsoft.EntityFrameworkCore;
using Veneta.Assessments.Backend.ConsumerService.ReadModel;

namespace Veneta.Assessments.Backend.ConsumerService.Repository;

/// <summary>
/// Stores the derived Consumer read model.
/// </summary>
public sealed class AppDbContext : DbContext
{
    /// <summary>
    /// Gets projected Consumers.
    /// </summary>
    public DbSet<ConsumerReadModel> Consumers => Set<ConsumerReadModel>();

    /// <summary>
    /// Initializes the read model context.
    /// </summary>
    /// <param name="options">EF Core configuration.</param>
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options) { }
}
