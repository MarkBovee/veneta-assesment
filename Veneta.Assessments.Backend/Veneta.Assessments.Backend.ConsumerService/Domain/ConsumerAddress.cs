namespace Veneta.Assessments.Backend.ConsumerService.Domain;

/// <summary>
/// Immutable address belonging to a Consumer.
/// </summary>
public sealed record ConsumerAddress(string Street, string PostalCode, string HouseNumber);
