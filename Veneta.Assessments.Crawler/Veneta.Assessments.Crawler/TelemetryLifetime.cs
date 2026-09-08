using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Veneta.Assessments.Crawler;

/// <summary>
/// Disposes crawler OpenTelemetry providers together.
/// </summary>
public sealed class TelemetryLifetime(MeterProvider meterProvider, TracerProvider tracerProvider) : IDisposable
{
    /// <summary>
    /// Flushes and disposes metrics and traces.
    /// </summary>
    public void Dispose()
    {
        tracerProvider.Dispose();
        meterProvider.Dispose();
    }
}
