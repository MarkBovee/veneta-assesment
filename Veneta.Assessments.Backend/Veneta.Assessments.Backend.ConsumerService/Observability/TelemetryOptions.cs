namespace Veneta.Assessments.Backend.ConsumerService.Observability;

/// <summary>
/// Controls optional payload capture for development traces.
/// </summary>
public static class TelemetryOptions
{
    /// <summary>
    /// Gets or sets whether request and response objects may be attached to spans.
    /// </summary>
    public static bool CapturePayloads { get; set; }
}
