namespace Veneta.Assessments.Backend.ConsumerService.Domain;

/// <summary>
/// Records a Consumer name change.
/// </summary>
public sealed record ConsumerNameChanged(Guid ConsumerId, string FirstName, string LastName) : IConsumerEvent;
