namespace Veneta.Assessments.Backend.ConsumerService.Domain;

/// <summary>
/// Immutable fact in a Consumer event stream.
/// </summary>
public interface IConsumerEvent
{
    /// <summary>
    /// Gets Consumer stream identifier.
    /// </summary>
    Guid ConsumerId { get; }
}
