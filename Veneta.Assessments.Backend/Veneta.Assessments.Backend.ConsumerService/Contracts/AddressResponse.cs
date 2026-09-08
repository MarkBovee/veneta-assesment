namespace Veneta.Assessments.Backend.ConsumerService.Contracts;

/// <summary>
/// Address returned by Consumer API.
/// </summary>
public sealed record AddressResponse(string Street, string PostalCode, string HouseNumber);
