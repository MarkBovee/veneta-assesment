namespace Veneta.Assessments.Crawler;

/// <summary>
/// Consumer contract received over HTTP.
/// </summary>
public sealed record ConsumerResponse(Guid Id, int Version, string FirstName, string LastName, AddressResponse Address);
