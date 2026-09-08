namespace Veneta.Assessments.Crawler;

/// <summary>
/// Consumer page contract received over HTTP.
/// </summary>
public sealed record ConsumerPageResponse(IReadOnlyList<ConsumerResponse> Items, int Offset, int Limit, int Total);
