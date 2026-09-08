namespace Veneta.Assessments.Backend.ConsumerService.Domain;

/// <summary>
/// Records a Consumer address change.
/// </summary>
public sealed record ConsumerAddressChanged(Guid ConsumerId, ConsumerAddress Address) : IConsumerEvent;
