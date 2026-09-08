namespace Veneta.Assessments.Backend.ConsumerService.Contracts;

/// <summary>
/// Address supplied in Consumer API commands.
/// </summary>
public sealed record AddressRequest(string Street, string PostalCode, string HouseNumber);
