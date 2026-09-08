namespace Veneta.Assessments.Backend.ConsumerService.Contracts;

/// <summary>
/// Request for an address change at a known stream version.
/// </summary>
public sealed record ChangeConsumerAddressRequest(AddressRequest Address, int ExpectedVersion);
