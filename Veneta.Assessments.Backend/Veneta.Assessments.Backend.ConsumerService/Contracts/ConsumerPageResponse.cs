namespace Veneta.Assessments.Backend.ConsumerService.Contracts;

/// <summary>
/// Bounded Consumer query page.
/// </summary>
public sealed record ConsumerPageResponse(IReadOnlyList<ConsumerResponse> Items, int Offset, int Limit, int Total);
