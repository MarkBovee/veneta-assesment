namespace Veneta.Assessments.Backend.ConsumerService.Contracts;

/// <summary>
/// Consumer returned by Consumer API.
/// </summary>
public sealed record ConsumerResponse(Guid Id, int Version, string FirstName, string LastName, AddressResponse Address);
