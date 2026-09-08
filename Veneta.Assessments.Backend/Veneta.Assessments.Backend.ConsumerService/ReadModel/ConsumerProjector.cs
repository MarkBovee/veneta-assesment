using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NEventStore;
using Veneta.Assessments.Backend.ConsumerService.Application;
using Veneta.Assessments.Backend.ConsumerService.Domain;
using Veneta.Assessments.Backend.ConsumerService.Observability;
using Veneta.Assessments.Backend.ConsumerService.Repository;

namespace Veneta.Assessments.Backend.ConsumerService.ReadModel;

/// <summary>
/// Applies persisted Consumer events to the derived read model.
/// </summary>
public sealed class ConsumerProjector
{
    private static readonly ActivitySource ActivitySource = new("Veneta.Assessments.Backend.Projection");
    private static readonly SemaphoreSlim ProjectionLock = new(1, 1);
    private readonly AppDbContext _context;
    private readonly IStoreEvents _store;

    /// <summary>
    /// Initializes the projector.
    /// </summary>
    /// <param name="context">Read model database context.</param>
    /// <param name="store">Authoritative Consumer event store.</param>
    public ConsumerProjector(AppDbContext context, IStoreEvents store)
    {
        _context = context;
        _store = store;
    }

    /// <summary>
    /// Projects one committed commit, replaying its stream only when read state has a gap.
    /// </summary>
    /// <param name="commit">Persisted commit to apply.</param>
    /// <param name="cancellationToken">Operation cancellation token.</param>
    public async Task ProjectAsync(ICommit commit, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("projection.project_commit", ActivityKind.Internal);
        activity?.SetTag("eventstore.stream_id", commit.StreamId);
        activity?.SetTag("eventstore.revision", commit.StreamRevision);
        activity?.SetTag("eventstore.event_count", commit.Events.Count);
        var lockAcquired = false;
        var lockWaitStarted = Stopwatch.GetTimestamp();

        try
        {
            await ProjectionLock.WaitAsync(cancellationToken);
            lockAcquired = true;
            activity?.SetTag("projection.lock_wait_ms", Stopwatch.GetElapsedTime(lockWaitStarted).TotalMilliseconds);
            var consumerId = ParseConsumerId(commit.StreamId);
            var consumer = await _context.Consumers.FindAsync([consumerId], cancellationToken);
            activity?.AddEvent(new ActivityEvent("projection.read_model_loaded", tags: new ActivityTagsCollection { ["read_model.version"] = consumer?.Version ?? 0, ["read_model.exists"] = consumer is not null }));

            // Ignore commits already represented by the read model.
            if (consumer is not null && consumer.Version >= commit.StreamRevision)
            {
                activity?.SetTag("projection.action", "skip_already_applied");
                AttachProjectionOutput(activity, consumer);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return;
            }

            // Replay authoritative history when the next revision is not contiguous.
            if (consumer is null && commit.StreamRevision != 1 || consumer is not null && consumer.Version + 1 != commit.StreamRevision)
            {
                activity?.SetTag("projection.action", "rebuild_gap");
                await RebuildConsumerAsync(commit.StreamId, cancellationToken);
                await ApplyCommitAsync(commit, cancellationToken);
                await AttachProjectionOutputAsync(activity, consumerId, cancellationToken);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return;
            }

            activity?.SetTag("projection.action", "apply_commit");
            await ApplyCommitAsync(commit, cancellationToken);
            await AttachProjectionOutputAsync(activity, consumerId, cancellationToken);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        finally
        {
            if (lockAcquired)
            {
                ProjectionLock.Release();
            }
        }
    }

    /// <summary>
    /// Replays one stream when direct commit projection cannot safely continue.
    /// </summary>
    /// <param name="streamId">Consumer stream identifier.</param>
    /// <param name="cancellationToken">Operation cancellation token.</param>
    private async Task RebuildConsumerAsync(string streamId, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("projection.rebuild_consumer", ActivityKind.Internal);
        activity?.SetTag("eventstore.stream_id", streamId);
        var consumerId = ParseConsumerId(streamId);
        var existing = await _context.Consumers.FindAsync([consumerId], cancellationToken);

        // Remove stale derived state before replaying authoritative history.
        if (existing is not null)
        {
            _context.Consumers.Remove(existing);
        }

        await _context.SaveChangesAsync(cancellationToken);

        // Replay authoritative commits in stream order to rebuild derived state.
        var replayedCommits = 0;
        foreach (var commit in _store.Advanced.GetFrom("consumer", streamId, 0, int.MaxValue))
        {
            await ApplyCommitAsync(commit, cancellationToken);
            replayedCommits++;
        }

        activity?.SetTag("projection.replayed_commits", replayedCommits);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    /// <summary>
    /// Applies one ordered event-store commit to the read model.
    /// </summary>
    /// <param name="commit">Commit to apply.</param>
    /// <param name="cancellationToken">Operation cancellation token.</param>
    private async Task ApplyCommitAsync(ICommit commit, CancellationToken cancellationToken)
    {
        var consumerId = ParseConsumerId(commit.StreamId);
        var consumer = await _context.Consumers.FindAsync([consumerId], cancellationToken);

        // Keep projection idempotent when a commit is delivered more than once.
        if (consumer is not null && consumer.Version >= commit.StreamRevision)
        {
            return;
        }

        foreach (var message in commit.Events)
        {
            switch (message.Body)
            {
                case ConsumerCreated created:
                    consumer = new ConsumerReadModel
                    {
                        Id = created.ConsumerId,
                        FirstName = created.FirstName,
                        LastName = created.LastName,
                        Street = created.Address.Street,
                        PostalCode = created.Address.PostalCode,
                        HouseNumber = created.Address.HouseNumber,
                        Version = commit.StreamRevision,
                    };
                    _context.Consumers.Add(consumer);
                    break;
                case ConsumerNameChanged changed when consumer is not null:
                    consumer.FirstName = changed.FirstName;
                    consumer.LastName = changed.LastName;
                    consumer.Version = commit.StreamRevision;
                    break;
                case ConsumerAddressChanged changed when consumer is not null:
                    consumer.Street = changed.Address.Street;
                    consumer.PostalCode = changed.Address.PostalCode;
                    consumer.HouseNumber = changed.Address.HouseNumber;
                    consumer.Version = commit.StreamRevision;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported Consumer event '{message.Body.GetType().Name}'.");
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Loads and attaches the projected Consumer object to a development trace.
    /// </summary>
    private async Task AttachProjectionOutputAsync(Activity? activity, Guid consumerId, CancellationToken cancellationToken)
    {
        var consumer = await _context.Consumers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == consumerId, cancellationToken);
        AttachProjectionOutput(activity, consumer);
    }

    /// <summary>
    /// Serializes the projected Consumer when payload capture is enabled.
    /// </summary>
    private static void AttachProjectionOutput(Activity? activity, ConsumerReadModel? consumer)
    {
        if (!TelemetryOptions.CapturePayloads || activity is null || consumer is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(consumer, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        activity.SetTag("projection.output.json", json);
        activity.AddEvent(new ActivityEvent("projection.output", tags: new ActivityTagsCollection { ["projection.output.json"] = json }));
    }

    /// <summary>
    /// Parses a Consumer stream identifier and reports malformed event-store data clearly.
    /// </summary>
    /// <param name="streamId">NEventStore stream identifier.</param>
    /// <returns>Consumer identifier.</returns>
    private static Guid ParseConsumerId(string streamId) => Guid.TryParse(streamId, out var consumerId) ? consumerId : throw new InvalidOperationException($"Consumer stream '{streamId}' has an invalid identifier.");

    /// <summary>
    /// Rebuilds read state by applying each authoritative commit once in storage order.
    /// </summary>
    /// <param name="commits">All Consumer commits in checkpoint order.</param>
    /// <param name="cancellationToken">Operation cancellation token.</param>
    public async Task RebuildAsync(IEnumerable<ICommit> commits, CancellationToken cancellationToken)
    {
        var lockAcquired = false;

        try
        {
            await ProjectionLock.WaitAsync(cancellationToken);
            lockAcquired = true;
            await _context.Consumers.ExecuteDeleteAsync(cancellationToken);
            _context.ChangeTracker.Clear();

            // Rebuild the derived read model from authoritative commits in order.
            foreach (var commit in commits)
            {
                await ApplyCommitAsync(commit, cancellationToken);
            }
        }
        finally
        {
            if (lockAcquired)
            {
                ProjectionLock.Release();
            }
        }
    }
}
