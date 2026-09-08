using Veneta.Assessments.Backend.ConsumerService.Domain;

namespace Veneta.Assessments.Backend.ConsumerService.Tests;

/// <summary>
/// Tests Consumer aggregate command and replay behaviour.
/// </summary>
public sealed class ConsumerTests
{
    /// <summary>
    /// Creates Consumer as a single initial fact.
    /// </summary>
    [Fact]
    public void Create_RaisesCreatedEventAndSetsVersion()
    {
        var consumer = Consumer.Create(Guid.NewGuid(), "Mark", "Bovee", new ConsumerAddress("Main", "1234AB", "1"));

        var created = Assert.IsType<ConsumerCreated>(Assert.Single(consumer.UncommittedEvents));
        Assert.Equal(consumer.Id, created.ConsumerId);
        Assert.Equal(1, consumer.Version);
    }

    /// <summary>
    /// Replays state without tracking historical facts for persistence.
    /// </summary>
    [Fact]
    public void Rehydrate_RebuildsStateWithoutUncommittedEvents()
    {
        var id = Guid.NewGuid();
        var consumer = Consumer.Rehydrate(
            [new ConsumerCreated(id, "Mark", "Bovee", new ConsumerAddress("Main", "1234AB", "1")), new ConsumerNameChanged(id, "Markus", "Bovee"), new ConsumerAddressChanged(id, new ConsumerAddress("Other", "5678CD", "2"))]
        );

        Assert.Equal(3, consumer.Version);
        Assert.Equal("Markus", consumer.FirstName);
        Assert.Equal("Other", consumer.Address.Street);
        Assert.Empty(consumer.UncommittedEvents);
    }

    /// <summary>
    /// Rejects invalid Consumer changes before event persistence.
    /// </summary>
    [Fact]
    public void ChangeName_RejectsBlankFirstName()
    {
        var consumer = Consumer.Create(Guid.NewGuid(), "Mark", "Bovee", new ConsumerAddress("Main", "1234AB", "1"));

        Assert.Throws<ArgumentException>(() => consumer.ChangeName(" ", "Bovee"));
    }

    /// <summary>
    /// Rejects malformed event history.
    /// </summary>
    [Fact]
    public void Rehydrate_RejectsChangeBeforeCreation()
    {
        Assert.Throws<InvalidOperationException>(() => Consumer.Rehydrate([new ConsumerNameChanged(Guid.NewGuid(), "Mark", "Bovee")]));
    }
}
