namespace Veneta.Assessments.Backend.ConsumerService.Contracts;

/// <summary>
/// Request for a name change at a known stream version.
/// </summary>
public sealed record ChangeConsumerNameRequest(string FirstName, string LastName, int ExpectedVersion);
