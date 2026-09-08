using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NEventStore;
using Veneta.Assessments.Backend.ConsumerService.Domain;
using Veneta.Assessments.Backend.ConsumerService.Observability;
using Veneta.Assessments.Backend.ConsumerService.ReadModel;
using Veneta.Assessments.Backend.ConsumerService.Repository;

namespace Veneta.Assessments.Backend.ConsumerService.Application;

/// <summary>
/// Coordinates Consumer commands and read-side queries.
/// </summary>
public sealed class ConsumerApplicationService
{
    public const string ActivitySourceName = "Veneta.Assessments.Backend";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly IStoreEvents _store;
    private readonly AppDbContext _context;
    private readonly ConsumerProjector _projector;
    private readonly ILogger<ConsumerApplicationService> _logger;

    /// <summary>
    /// Initializes application orchestration dependencies.
    /// </summary>
    /// <param name="store">Authoritative event store.</param>
    /// <param name="context">Derived read model database.</param>
    /// <param name="projector">Read model projector.</param>
    /// <param name="logger">Operational logger.</param>
    public ConsumerApplicationService(IStoreEvents store, AppDbContext context, ConsumerProjector projector, ILogger<ConsumerApplicationService> logger)
    {
        _store = store;
        _context = context;
        _projector = projector;
        _logger = logger;
    }

    /// <summary>
    /// Creates and persists a Consumer aggregate.
    /// </summary>
    /// <param name="consumer">New aggregate.</param>
    /// <param name="cancellationToken">Operation cancellation token.</param>
    /// <returns>Persisted aggregate.</returns>
    public async Task<Consumer> CreateAsync(Consumer consumer, CancellationToken cancellationToken)
    {
        // Start a new activity for tracing the consumer creation operation.
        using var activity = ActivitySource.StartActivity("consumer.create", ActivityKind.Internal);
        activity?.SetTag("consumer.id", consumer.Id);

        // Append the aggregate's uncommitted events to a new authoritative stream.
        using var stream = _store.CreateStream("consumer", consumer.Id);
        AddEvents(stream, consumer.UncommittedEvents);

        // Commit the events before touching the derived read model.
        var commit = await stream.CommitChangesAsync(Guid.NewGuid(), cancellationToken) ?? throw new InvalidOperationException("Event commit was not persisted.");

        // Record commit metadata so the event-store operation is visible in traces.
        activity?.SetTag("eventstore.revision", commit.StreamRevision);
        activity?.SetTag("eventstore.event_count", commit.Events.Count);
        activity?.SetTag("eventstore.event_types", string.Join(',', commit.Events.Select(message => message.Body.GetType().Name)));
        AttachEventPayloads(activity, commit);
        activity?.AddEvent(new ActivityEvent("eventstore.commit_persisted", tags: new ActivityTagsCollection { ["eventstore.revision"] = commit.StreamRevision, ["eventstore.event_count"] = commit.Events.Count }));

        // Clear the aggregate's pending event buffer after the commit succeeds.
        consumer.MarkChangesAsCommitted();

        // Project the committed events into the disposable read model.
        await ProjectCommittedAsync(commit, cancellationToken);

        activity?.SetStatus(ActivityStatusCode.Ok);
        return consumer;
    }

    /// <summary>
    /// Loads, changes, persists, and projects one Consumer.
    /// </summary>
    /// <param name="id">Consumer identifier.</param>
    /// <param name="expectedVersion">Version observed by caller.</param>
    /// <param name="change">Domain command.</param>
    /// <param name="cancellationToken">Operation cancellation token.</param>
    public async Task ChangeAsync(Guid id, int expectedVersion, Action<Consumer> change, CancellationToken cancellationToken)
    {
        // Start a new activity for tracing the consumer change operation.
        using var activity = ActivitySource.StartActivity("consumer.change", ActivityKind.Internal);
        activity?.SetTag("consumer.id", id);
        activity?.SetTag("consumer.expected_version", expectedVersion);

        // Open the event stream for the specified consumer.
        using var stream = await _store.OpenStreamAsync("consumer", id.ToString(), 0, int.MaxValue, cancellationToken);
        // A newly created stream must contain the aggregate creation event.
        if (stream.CommittedEvents.Count == 0)
        {
            throw new KeyNotFoundException();
        }

        // Reject stale commands before changing aggregate state.
        if (stream.StreamRevision != expectedVersion)
        {
            throw new ConcurrencyException();
        }

        // Rehydrate the consumer aggregate from the committed events.
        var consumer = Consumer.Rehydrate(stream.CommittedEvents.Select(message => (IConsumerEvent)message.Body));

        // Apply the domain command to the consumer aggregate.
        change(consumer);
        AddEvents(stream, consumer.UncommittedEvents);

        // Persist the uncommitted events to the event store.
        var commit = await stream.CommitChangesAsync(Guid.NewGuid(), cancellationToken) ?? throw new InvalidOperationException("Event commit was not persisted.");

        // Record event store commit details for tracing.
        activity?.SetTag("eventstore.revision", commit.StreamRevision);
        activity?.SetTag("eventstore.event_count", commit.Events.Count);
        activity?.SetTag("eventstore.event_types", string.Join(',', commit.Events.Select(message => message.Body.GetType().Name)));
        AttachEventPayloads(activity, commit);

        // Emit an activity event indicating that the commit has been persisted.
        activity?.AddEvent(new ActivityEvent("eventstore.commit_persisted", tags: new ActivityTagsCollection { ["eventstore.revision"] = commit.StreamRevision, ["eventstore.event_count"] = commit.Events.Count }));

        // Mark the consumer aggregate's changes as committed and project the committed events.
        consumer.MarkChangesAsCommitted();

        // Project the committed events to update read models.
        await ProjectCommittedAsync(commit, cancellationToken);

        // Mark the activity as successfully completed.
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    /// <summary>
    /// Gets one projected Consumer.
    /// </summary>
    /// <param name="id">Consumer identifier.</param>
    /// <param name="cancellationToken">Operation cancellation token.</param>
    /// <returns>Projected Consumer, or null when absent.</returns>
    public async Task<ConsumerReadModel?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        // Start a new activity for tracing the read-model lookup.
        using var activity = ActivitySource.StartActivity("consumer.get", ActivityKind.Internal);
        activity?.SetTag("consumer.id", id);

        // Read from the derived model without tracking a write-side entity.
        var consumer = await _context.Consumers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        activity?.SetTag("consumer.found", consumer is not null);
        // Mark the lookup successful even when no matching consumer exists.
        activity?.SetStatus(ActivityStatusCode.Ok);
        return consumer;
    }

    /// <summary>
    /// Gets a bounded page of projected Consumers.
    /// </summary>
    /// <param name="offset">Zero-based offset.</param>
    /// <param name="limit">Maximum page size.</param>
    /// <param name="cancellationToken">Operation cancellation token.</param>
    /// <returns>Projected page items and total count.</returns>
    public async Task<(IReadOnlyList<ConsumerReadModel> Items, int Total)> GetPageAsync(int offset, int limit, CancellationToken cancellationToken)
    {
        // Start a new activity for tracing the paged consumer query.
        using var activity = ActivitySource.StartActivity("consumer.get_page", ActivityKind.Internal);
        activity?.SetTag("pagination.offset", offset);
        activity?.SetTag("pagination.limit", limit);

        // Read the total separately so the response can expose pagination metadata.
        var total = await _context.Consumers.CountAsync(cancellationToken);
        var items = await _context.Consumers.AsNoTracking().OrderBy(item => item.Id).Skip(offset).Take(limit).ToListAsync(cancellationToken);
        activity?.SetTag("pagination.total", total);
        activity?.SetTag("pagination.items", items.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);
        return (items, total);
    }

    /// <summary>
    /// Appends aggregate events to an open NEventStore stream.
    /// </summary>
    /// <param name="stream">Open stream session.</param>
    /// <param name="events">Events raised by aggregate command.</param>
    private static void AddEvents(IEventStream stream, IEnumerable<IConsumerEvent> events)
    {
        // Preserve aggregate event order when appending to the event stream.
        foreach (var @event in events)
        {
            stream.Add(new EventMessage { Body = @event });
        }
    }

    /// <summary>
    /// Projects committed state while preserving successful event commits.
    /// </summary>
    /// <param name="commit">Persisted event commit.</param>
    /// <param name="cancellationToken">Operation cancellation token.</param>
    private async Task ProjectCommittedAsync(ICommit commit, CancellationToken cancellationToken)
    {
        // Trace projection separately so event persistence remains distinguishable from read-model work.
        using var activity = ActivitySource.StartActivity("eventstore.project_commit", ActivityKind.Internal);
        activity?.SetTag("eventstore.stream_id", commit.StreamId);
        activity?.SetTag("eventstore.revision", commit.StreamRevision);
        activity?.SetTag("eventstore.event_count", commit.Events.Count);
        activity?.SetTag("eventstore.event_types", string.Join(',', commit.Events.Select(message => message.Body.GetType().Name)));
        AttachEventPayloads(activity, commit);

        try
        {
            // Projection failure must not rewrite or discard the committed event history.
            await _projector.ProjectAsync(commit, cancellationToken);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Consumer {ConsumerId} at version {Version} committed but projection failed; replay is required.", commit.StreamId, commit.StreamRevision);
        }
    }

    /// <summary>
    /// Attaches committed event objects to the development trace.
    /// </summary>
    private static void AttachEventPayloads(Activity? activity, ICommit commit)
    {
        // Payload capture is opt-in because event bodies may contain sensitive development data.
        if (!TelemetryOptions.CapturePayloads || activity is null)
        {
            return;
        }

        var payload = commit.Events.Select(message => message.Body).ToArray();
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        activity.SetTag("eventstore.events.json", json);
        activity.AddEvent(new ActivityEvent("eventstore.events", tags: new ActivityTagsCollection { ["eventstore.events.json"] = json }));
    }
}
