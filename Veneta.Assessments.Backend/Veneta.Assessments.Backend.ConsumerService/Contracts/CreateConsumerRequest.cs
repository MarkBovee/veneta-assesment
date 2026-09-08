namespace Veneta.Assessments.Backend.ConsumerService.Contracts;

/// <summary>
/// Request for Consumer creation.
/// </summary>
public sealed record CreateConsumerRequest(string FirstName, string LastName, AddressRequest Address);
