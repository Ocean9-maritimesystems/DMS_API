namespace DMS.Application.DTO.Features;

/// <summary>
/// Filters of a report (api/Reports/{key}). Each report uses the ones that apply to it and ignores the rest.
/// VesselId -1 = the office (acknowledgement reports). To is inclusive (the whole day).
/// </summary>
public sealed record ReportFilter(
    int? VesselId,
    string? ItemType,
    string? Status,
    DateTime? From,
    DateTime? To,
    bool OverdueOnly,
    int? Days,
    bool TopLevelOnly);
