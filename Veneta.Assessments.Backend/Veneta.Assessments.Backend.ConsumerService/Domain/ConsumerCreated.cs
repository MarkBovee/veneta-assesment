namespace Veneta.Assessments.Backend.ConsumerService.Domain;

/// <summary>
/// Records Consumer creation and initial data.
/// </summary>
public sealed record ConsumerCreated(Guid ConsumerId, string FirstName, string LastName, ConsumerAddress Address) : IConsumerEvent;
