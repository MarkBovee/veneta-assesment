namespace Veneta.Assessments.Backend.ConsumerService.Domain;

/// <summary>
/// Event-sourced aggregate that owns Consumer state and invariants.
/// </summary>
public sealed class Consumer
{
    private readonly List<IConsumerEvent> _uncommittedEvents = [];

    private Consumer() { }

    /// <summary>
    /// Gets Consumer identifier.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// Gets first name.
    /// </summary>
    public string FirstName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets last name.
    /// </summary>
    public string LastName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets current address.
    /// </summary>
    public ConsumerAddress Address { get; private set; } = null!;

    /// <summary>
    /// Gets version represented by applied events.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// Gets events that still need persistence.
    /// </summary>
    public IReadOnlyList<IConsumerEvent> UncommittedEvents => _uncommittedEvents;

    /// <summary>
    /// Creates Consumer and raises its initial event.
    /// </summary>
    /// <param name="id">New Consumer identifier.</param>
    /// <param name="firstName">Required first name.</param>
    /// <param name="lastName">Required last name.</param>
    /// <param name="address">Required address.</param>
    /// <returns>New aggregate.</returns>
    public static Consumer Create(Guid id, string firstName, string lastName, ConsumerAddress address)
    {
        // Validate through the same event path used when historical state is applied.
        var consumer = new Consumer();
        consumer.Raise(new ConsumerCreated(id, Required(firstName, nameof(firstName)), Required(lastName, nameof(lastName)), RequiredAddress(address)));
        return consumer;
    }

    /// <summary>
    /// Rehydrates Consumer from ordered historical events.
    /// </summary>
    /// <param name="events">Historical events.</param>
    /// <returns>Rehydrated aggregate without uncommitted events.</returns>
    public static Consumer Rehydrate(IEnumerable<IConsumerEvent> events)
    {
        var consumer = new Consumer();

        // Apply historical events in stream order without raising new events.
        foreach (var @event in events)
        {
            // Validate stream shape before applying each historical event.
            if (consumer.Version == 0 && @event is not ConsumerCreated)
            {
                throw new InvalidOperationException("Consumer stream must start with ConsumerCreated.");
            }

            if (consumer.Version > 0 && @event is ConsumerCreated)
            {
                throw new InvalidOperationException("Consumer stream cannot contain multiple creation events.");
            }

            if (consumer.Version > 0 && consumer.Id != @event.ConsumerId)
            {
                throw new InvalidOperationException("Consumer stream contains events for different Consumers.");
            }

            consumer.Apply(@event);
            consumer.Version++;
        }

        return consumer;
    }

    /// <summary>
    /// Changes Consumer name.
    /// </summary>
    /// <param name="firstName">Required first name.</param>
    /// <param name="lastName">Required last name.</param>
    public void ChangeName(string firstName, string lastName) => Raise(new ConsumerNameChanged(Id, Required(firstName, nameof(firstName)), Required(lastName, nameof(lastName))));

    /// <summary>
    /// Changes Consumer address.
    /// </summary>
    /// <param name="address">Required address.</param>
    public void ChangeAddress(ConsumerAddress address) => Raise(new ConsumerAddressChanged(Id, RequiredAddress(address)));

    /// <summary>
    /// Clears events after successful persistence.
    /// </summary>
    public void MarkChangesAsCommitted() => _uncommittedEvents.Clear();

    /// <summary>
    /// Applies and tracks an event created by a command.
    /// </summary>
    /// <param name="@event">New event.</param>
    private void Raise(IConsumerEvent @event)
    {
        // Apply the new state first so the aggregate version matches the pending event.
        Apply(@event);
        Version++;
        _uncommittedEvents.Add(@event);
    }

    /// <summary>
    /// Updates state from a fact without evaluating business rules.
    /// </summary>
    /// <param name="@event">Event to apply.</param>
    private void Apply(IConsumerEvent @event)
    {
        // Events are facts: applying them changes state without creating new events.
        switch (@event)
        {
            case ConsumerCreated created:
                Id = created.ConsumerId;
                FirstName = created.FirstName;
                LastName = created.LastName;
                Address = created.Address;
                break;
            case ConsumerNameChanged changed:
                FirstName = changed.FirstName;
                LastName = changed.LastName;
                break;
            case ConsumerAddressChanged changed:
                Address = changed.Address;
                break;
            default:
                throw new InvalidOperationException($"Unknown Consumer event '{@event.GetType().Name}'.");
        }
    }

    /// <summary>
    /// Validates and normalizes a required textual value.
    /// </summary>
    /// <param name="value">Value to validate.</param>
    /// <param name="name">Parameter name.</param>
    /// <returns>Trimmed value.</returns>
    private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();

    /// <summary>
    /// Validates each address component.
    /// </summary>
    /// <param name="address">Address to validate.</param>
    /// <returns>Normalized address.</returns>
    private static ConsumerAddress RequiredAddress(ConsumerAddress address) => new(Required(address?.Street!, "street"), Required(address?.PostalCode!, "postalCode"), Required(address?.HouseNumber!, "houseNumber"));
}
