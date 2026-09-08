namespace Veneta.Assessments.Backend.ConsumerService.Endpoints;

/// <summary>
/// Maps service-level endpoints that are independent of Consumer commands.
/// </summary>
public static class ConsumerServiceEndpoints
{
    /// <summary>
    /// Maps the liveness and readiness endpoints for the Consumer API.
    /// </summary>
    /// <param name="app">Web application receiving the routes.</param>
    /// <param name="readiness">Initialization completion source.</param>
    public static void MapConsumerServiceEndpoints(this WebApplication app, TaskCompletionSource readiness)
    {
        app.MapGet("/", () => Results.Ok(new { service = "consumer-api", status = "ok" }));
        app.MapGet("/ready", () => readiness.Task.IsCompletedSuccessfully ? Results.Ok(new { service = "consumer-api", status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        app.MapConsumerEndpoints();
    }
}
