namespace Veneta.Assessments.Crawler;

/// <summary>
/// Stores crawler-owned Consumer state.
/// </summary>
public sealed class CrawledConsumer
{
    /// <summary>
    /// Gets or sets Consumer identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets source version.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Gets or sets first name.
    /// </summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets last name.
    /// </summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets street.
    /// </summary>
    public string Street { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets postal code.
    /// </summary>
    public string PostalCode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets house number.
    /// </summary>
    public string HouseNumber { get; set; } = string.Empty;
}
