namespace Veneta.Assessments.Crawler;

/// <summary>
/// Address contract received over HTTP.
/// </summary>
public sealed record AddressResponse(string Street, string PostalCode, string HouseNumber);
