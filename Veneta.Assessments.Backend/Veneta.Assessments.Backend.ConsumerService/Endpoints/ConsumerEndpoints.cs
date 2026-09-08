using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NEventStore;
using Veneta.Assessments.Backend.ConsumerService.Application;
using Veneta.Assessments.Backend.ConsumerService.Contracts;
using Veneta.Assessments.Backend.ConsumerService.Domain;
using Veneta.Assessments.Backend.ConsumerService.ReadModel;

namespace Veneta.Assessments.Backend.ConsumerService.Endpoints;

/// <summary>
/// Defines HTTP endpoints for Consumer commands and queries.
/// </summary>
public static class ConsumerEndpoints
{
    /// <summary>
    /// Maps Consumer routes onto the application service.
    /// </summary>
    /// <param name="app">Application route builder.</param>
    public static void MapConsumerEndpoints(this WebApplication app)
    {
        // Group all Consumer routes under one stable resource prefix.
        var consumers = app.MapGroup("/consumers");
        consumers.MapPost(string.Empty, CreateAsync);
        consumers.MapPut("/{id:guid}/name", ChangeNameAsync);
        consumers.MapPut("/{id:guid}/address", ChangeAddressAsync);
        consumers.MapGet("/{id:guid}", GetAsync);
        consumers.MapGet(string.Empty, GetPageAsync);
    }

    /// <summary>
    /// Creates a Consumer and returns its persisted representation.
    /// </summary>
    private static async Task<IResult> CreateAsync(CreateConsumerRequest request, ConsumerApplicationService service, CancellationToken cancellationToken)
    {
        try
        {
            // Validate and construct the aggregate before entering the application service.
            var aggregate = Consumer.Create(Guid.NewGuid(), request.FirstName, request.LastName, ToAddress(request.Address));
            AttachPayload("api.request", request);

            // Persist the command through the event-sourced application boundary.
            var consumer = await service.CreateAsync(aggregate, cancellationToken);

            // Record the successful API result on the active request trace.
            AttachPayload("api.response", ToResponse(consumer));
            Activity.Current?.SetTag("consumer.operation", "create");
            Activity.Current?.SetTag("consumer.id", consumer.Id);
            Activity.Current?.SetTag("consumer.version", consumer.Version);
            Activity.Current?.SetTag("http.response.status_code", StatusCodes.Status201Created);
            Activity.Current?.AddEvent(new ActivityEvent("consumer.created"));
            return Results.Created($"/consumers/{consumer.Id}", ToResponse(consumer));
        }
        catch (ArgumentException exception)
        {
            // Translate domain validation failures into the public validation contract.
            RecordFailure("validation_error", StatusCodes.Status400BadRequest, exception);
            return Validation(exception);
        }
        catch (ConcurrencyException exception)
        {
            // Keep optimistic concurrency conflicts distinct from malformed input.
            RecordFailure("concurrency_conflict", StatusCodes.Status409Conflict, exception);
            return Results.Conflict();
        }
    }

    /// <summary>
    /// Changes a Consumer name using expected stream version.
    /// </summary>
    private static Task<IResult> ChangeNameAsync(Guid id, ChangeConsumerNameRequest request, ConsumerApplicationService service, CancellationToken cancellationToken) =>
        ChangeAsync(id, request.ExpectedVersion, consumer => consumer.ChangeName(request.FirstName, request.LastName), service, cancellationToken);

    /// <summary>
    /// Changes a Consumer address using expected stream version.
    /// </summary>
    private static Task<IResult> ChangeAddressAsync(Guid id, ChangeConsumerAddressRequest request, ConsumerApplicationService service, CancellationToken cancellationToken) =>
        ChangeAsync(id, request.ExpectedVersion, consumer => consumer.ChangeAddress(ToAddress(request.Address)), service, cancellationToken);

    /// <summary>
    /// Loads a projected Consumer.
    /// </summary>
    private static async Task<IResult> GetAsync(Guid id, ConsumerApplicationService service, CancellationToken cancellationToken)
    {
        // Query the derived read model and preserve the not-found distinction in the response.
        var consumer = await service.GetAsync(id, cancellationToken);
        if (consumer is not null)
        {
            AttachPayload("api.response", ToResponse(consumer));
        }
        Activity.Current?.SetTag("consumer.operation", "get");
        Activity.Current?.SetTag("consumer.id", id);
        Activity.Current?.SetTag("consumer.found", consumer is not null);
        Activity.Current?.SetTag("http.response.status_code", consumer is null ? StatusCodes.Status404NotFound : StatusCodes.Status200OK);
        return consumer is null ? Results.NotFound() : Results.Ok(ToResponse(consumer));
    }

    /// <summary>
    /// Loads a bounded page of projected Consumers.
    /// </summary>
    private static async Task<IResult> GetPageAsync(int offset, int limit, ConsumerApplicationService service, CancellationToken cancellationToken)
    {
        // Reject pagination values outside the documented API bounds.
        if (offset < 0 || limit is < 1 or > 100)
        {
            Activity.Current?.SetTag("consumer.operation", "get_page");
            Activity.Current?.SetTag("pagination.valid", false);
            Activity.Current?.SetTag("http.response.status_code", StatusCodes.Status400BadRequest);
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["pagination"] = ["offset must be non-negative and limit must be between 1 and 100."] });
        }

        // Query the bounded page only after the request parameters pass validation.
        var page = await service.GetPageAsync(offset, limit, cancellationToken);
        // Map domain read models to the stable HTTP response contract.
        var response = new ConsumerPageResponse(page.Items.Select(ToResponse).ToList(), offset, limit, page.Total);
        AttachPayload("api.response", response);
        Activity.Current?.SetTag("consumer.operation", "get_page");
        Activity.Current?.SetTag("pagination.valid", true);
        Activity.Current?.SetTag("pagination.offset", offset);
        Activity.Current?.SetTag("pagination.limit", limit);
        Activity.Current?.SetTag("pagination.total", page.Total);
        Activity.Current?.SetTag("pagination.items", page.Items.Count);
        Activity.Current?.SetTag("http.response.status_code", StatusCodes.Status200OK);
        return Results.Ok(response);
    }

    /// <summary>
    /// Executes one update command and maps expected failures to HTTP.
    /// </summary>
    private static async Task<IResult> ChangeAsync(Guid id, int expectedVersion, Action<Consumer> change, ConsumerApplicationService service, CancellationToken cancellationToken)
    {
        // Expected versions must identify an existing stream revision.
        if (expectedVersion < 1)
        {
            Activity.Current?.SetTag("consumer.operation", "change");
            Activity.Current?.SetTag("consumer.id", id);
            Activity.Current?.SetTag("consumer.expected_version", expectedVersion);
            Activity.Current?.SetTag("http.response.status_code", StatusCodes.Status400BadRequest);
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["expectedVersion"] = ["expectedVersion must be positive."] });
        }
        try
        {
            // Attach the command context before applying the domain change.
            AttachPayload("api.request", new { id, expectedVersion });
            await service.ChangeAsync(id, expectedVersion, change, cancellationToken);
            Activity.Current?.SetTag("consumer.operation", "change");
            Activity.Current?.SetTag("consumer.id", id);
            Activity.Current?.SetTag("consumer.expected_version", expectedVersion);
            Activity.Current?.SetTag("http.response.status_code", StatusCodes.Status204NoContent);
            Activity.Current?.AddEvent(new ActivityEvent("consumer.changed"));
            return Results.NoContent();
        }
        catch (KeyNotFoundException exception)
        {
            // Report missing event streams as resource-not-found responses.
            RecordFailure("not_found", StatusCodes.Status404NotFound, exception);
            return Results.NotFound();
        }
        catch (ArgumentException exception)
        {
            // Report domain validation failures using the API's ProblemDetails shape.
            RecordFailure("validation_error", StatusCodes.Status400BadRequest, exception);
            return Validation(exception);
        }
        catch (ConcurrencyException exception)
        {
            // Report stale writes as conflicts so callers can refresh their version.
            RecordFailure("concurrency_conflict", StatusCodes.Status409Conflict, exception);
            return Results.Conflict();
        }
    }

    /// <summary>
    /// Builds validation ProblemDetails from domain input failure.
    /// </summary>
    private static IResult Validation(ArgumentException exception) => Results.ValidationProblem(new Dictionary<string, string[]> { [exception.ParamName ?? "request"] = [exception.Message] });

    /// <summary>
    /// Annotates the active HTTP span with a handled API failure.
    /// </summary>
    private static void RecordFailure(string outcome, int statusCode, Exception exception)
    {
        Activity.Current?.SetTag("request.outcome", outcome);
        Activity.Current?.SetTag("http.response.status_code", statusCode);
        Activity.Current?.AddEvent(new ActivityEvent("request.failure", tags: new ActivityTagsCollection { ["failure.type"] = exception.GetType().Name, ["failure.message"] = exception.Message }));
    }

    /// <summary>
    /// Attaches a JSON request or response object to the active demo trace.
    /// </summary>
    private static void AttachPayload(string attributeName, object payload)
    {
        // Payload capture is optional because trace bodies can contain sensitive data.
        if (!Observability.TelemetryOptions.CapturePayloads || Activity.Current is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Activity.Current.SetTag($"{attributeName}.json", json);
        Activity.Current.AddEvent(new ActivityEvent(attributeName, tags: new ActivityTagsCollection { [$"{attributeName}.json"] = json }));
    }

    /// <summary>
    /// Maps an API address into the domain value object.
    /// </summary>
    private static ConsumerAddress ToAddress(AddressRequest? address) => address is null ? throw new ArgumentException("Address is required.", nameof(address)) : new(address.Street, address.PostalCode, address.HouseNumber);

    /// <summary>
    /// Maps aggregate state to API response.
    /// </summary>
    private static ConsumerResponse ToResponse(Consumer consumer) =>
        new(consumer.Id, consumer.Version, consumer.FirstName, consumer.LastName, new AddressResponse(consumer.Address.Street, consumer.Address.PostalCode, consumer.Address.HouseNumber));

    /// <summary>
    /// Maps projected state to API response.
    /// </summary>
    private static ConsumerResponse ToResponse(ConsumerReadModel consumer) => new(consumer.Id, consumer.Version, consumer.FirstName, consumer.LastName, new AddressResponse(consumer.Street, consumer.PostalCode, consumer.HouseNumber));
}
