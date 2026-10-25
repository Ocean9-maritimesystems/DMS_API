using DMS.Application.DTO.Features;
using DMS.Application.Interface;
using DMS.Data.EF.Features;

namespace DMS.Infrastructure.Services;

/// <summary>
/// The office reports (Reports page). Each report returns rows as column -> value; the page decides which columns it
/// shows and exports them to Excel, PDF or CSV. The acknowledgement reports come from IAckDashboardService; the others
/// are stored procedures dbo.Rpt_* (Database/020_ApiProcedures.sql), whose column names are camelCase so they reach
/// the page as is.
///
///   ack-pending          crew who have not acknowledged a manual / circular / alert yet (dbo.Ack_GetPending)
///   ack-vessel-summary   pending and overdue acknowledgements per vessel, with the last sync
///   ack-office           office users who have not acknowledged yet
///   ack-completed        acknowledgements received (DM_Manual_UserAck, CIR_UserAck)
///   circular-register    circulars and alerts with status, dates and acknowledgement progress
///   document-register    other documents per vessel, where they came from and their files
///   manual-register      manuals with category, status and current version
///   vessel-sync          last contact / sync of every vessel (dbo.Sync_Vessel)
///   sync-log             sync history (dbo.Sync_Log)
///   suggestions          suggestions with status, vessel and what they are about
/// </summary>
public sealed class ReportService : IReportService
{
    /// <summary>The most rows a report returns, so a very wide filter cannot overload the browser.</summary>
    private const int MaxRows = 20000;

    private readonly SqlDb _db;
    private readonly IAckDashboardService _acks;

    public ReportService(SqlDb db, IAckDashboardService acks)
    {
        _db = db;
        _acks = acks;
    }

    public static readonly string[] Keys =
    {
        "ack-pending", "ack-vessel-summary", "ack-office", "ack-completed", "circular-register",
        "document-register", "manual-register", "vessel-sync", "sync-log", "suggestions",
    };

    public async Task<List<Dictionary<string, object?>>> RunAsync(string key, ReportFilter f)
    {
        try
        {
            return await RunReportAsync(key, f);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 50000)
        {
            // A message raised by a report procedure for the user (e.g. the sync tables are missing).
            throw new ArgumentException(ex.Message);
        }
    }

    private Task<List<Dictionary<string, object?>>> RunReportAsync(string key, ReportFilter f) => key.ToLowerInvariant() switch
    {
        "ack-pending" => AckPendingAsync(f),
        "ack-vessel-summary" => AckVesselSummaryAsync(f),
        "ack-office" => AckOfficeAsync(f),
        "ack-completed" => AckCompletedAsync(f),
        "circular-register" => CircularRegisterAsync(f),
        "document-register" => DocumentRegisterAsync(f),
        "manual-register" => ManualRegisterAsync(f),
        "vessel-sync" => VesselSyncAsync(f),
        "sync-log" => SyncLogAsync(f),
        "suggestions" => SuggestionsAsync(f),
        _ => throw new KeyNotFoundException($"There is no report '{key}'."),
    };

    // ------------------------------------------------------------------------------------------------ helpers

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The filter's dates: from the start of From to the end of To.</summary>
    private static (object? From, object? To) Range(ReportFilter f)
        => (f.From?.Date, f.To?.Date.AddDays(1));

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values)
    {
        var row = new Dictionary<string, object?>(values.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values) row[k] = v;
        return row;
    }

    // ------------------------------------------------------------------------------------------------ acknowledgements

    private async Task<List<Dictionary<string, object?>>> AckPendingAsync(ReportFilter f)
    {
        var vessel = f.VesselId is > 0 ? f.VesselId : null;
        var rows = await _acks.GetVesselPendingAsync(vessel, Text(f.ItemType), null);
        var status = Text(f.Status);
        return rows
            .Where(r => !f.OverdueOnly || r.Overdue)
            .Where(r => status == null || string.Equals(r.Rank, status, StringComparison.OrdinalIgnoreCase))
            .Take(MaxRows)
            .Select(r => Row(
                ("vessel", r.VesselName), ("type", r.ItemType), ("number", r.Number), ("title", r.Title), ("version", r.Version),
                ("rank", r.Rank), ("name", r.UserName), ("released", r.ReleasedOn), ("due", r.DueOn),
                ("daysWaiting", r.DaysWaiting), ("overdue", r.Overdue ? "Yes" : "No")))
            .ToList();
    }

    private async Task<List<Dictionary<string, object?>>> AckVesselSummaryAsync(ReportFilter f)
    {
        var rows = await _acks.GetVesselSummaryAsync();
        return rows
            .Where(r => f.VesselId is not > 0 || r.VesselID == f.VesselId)
            .Select(r => Row(
                ("vessel", r.VesselName), ("lastSync", r.LastSyncUtc),
                ("manualsPending", r.ManualsPending), ("manualsOverdue", r.ManualsOverdue),
                ("circularsPending", r.CircularsPending), ("circularsOverdue", r.CircularsOverdue),
                ("alertsPending", r.AlertsPending), ("alertsOverdue", r.AlertsOverdue),
                ("totalPending", r.ManualsPending + r.CircularsPending + r.AlertsPending),
                ("totalOverdue", r.ManualsOverdue + r.CircularsOverdue + r.AlertsOverdue)))
            .ToList();
    }

    private async Task<List<Dictionary<string, object?>>> AckOfficeAsync(ReportFilter f)
    {
        var rows = await _acks.GetOfficePendingAsync(null);
        var type = Text(f.ItemType);
        return rows
            .Where(r => type == null || string.Equals(r.ItemType, type, StringComparison.OrdinalIgnoreCase))
            .Where(r => !f.OverdueOnly || r.Overdue)
            .Take(MaxRows)
            .Select(r => Row(
                ("type", r.ItemType), ("number", r.Number), ("title", r.Title), ("version", r.Version),
                ("name", r.UserName), ("role", r.Rank), ("dept", r.Dept), ("released", r.ReleasedOn), ("due", r.DueOn),
                ("daysWaiting", r.DaysWaiting), ("overdue", r.Overdue ? "Yes" : "No")))
            .ToList();
    }

    private Task<List<Dictionary<string, object?>>> AckCompletedAsync(ReportFilter f)
    {
        var (from, to) = Range(f);
        return _db.ProcRowsAsync("dbo.Rpt_AckCompleted",
            ("@Max", MaxRows), ("@Type", Text(f.ItemType)), ("@VesselID", f.VesselId), ("@From", from), ("@To", to));
    }

    // ------------------------------------------------------------------------------------------------ circulars

    private async Task<List<Dictionary<string, object?>>> CircularRegisterAsync(ReportFilter f)
    {
        var (from, to) = Range(f);
        var rows = await _db.ProcRowsAsync("dbo.Rpt_CircularRegister",
            ("@Max", MaxRows), ("@Type", Text(f.ItemType)), ("@Status", Text(f.Status)), ("@From", from), ("@To", to));

        // Still to acknowledge, on board and at the office (the same rules as the Pending acks page).
        var pending = (await _acks.GetVesselPendingAsync(null, null, null))
            .Concat(await _acks.GetOfficePendingAsync(null))
            .Where(p => p.ItemType is "Circular" or "Alert")
            .GroupBy(p => p.ItemID)
            .ToDictionary(g => g.Key, g => (Pending: g.Count(), Overdue: g.Count(p => p.Overdue)));

        foreach (var row in rows)
        {
            var id = Convert.ToInt64(row["id"]);
            var p = pending.TryGetValue(id, out var counts) ? counts : (Pending: 0, Overdue: 0);
            row["acksPending"] = p.Pending;
            row["acksOverdue"] = p.Overdue;
        }
        return rows;
    }

    // ------------------------------------------------------------------------------------------------ documents

    private Task<List<Dictionary<string, object?>>> DocumentRegisterAsync(ReportFilter f)
    {
        var (from, to) = Range(f);
        return _db.ProcRowsAsync("dbo.Rpt_DocumentRegister",
            ("@Max", MaxRows), ("@VesselID", f.VesselId), ("@Type", Text(f.ItemType)), ("@Status", Text(f.Status)), ("@From", from), ("@To", to));
    }

    // ------------------------------------------------------------------------------------------------ manuals, sync, suggestions

    private Task<List<Dictionary<string, object?>>> ManualRegisterAsync(ReportFilter f)
        => _db.ProcRowsAsync("dbo.Rpt_ManualRegister",
            ("@Max", MaxRows), ("@Status", Text(f.Status)), ("@TopLevelOnly", f.TopLevelOnly));

    private Task<List<Dictionary<string, object?>>> VesselSyncAsync(ReportFilter f)
        => _db.ProcRowsAsync("dbo.Rpt_VesselSync", ("@VesselID", f.VesselId));

    private Task<List<Dictionary<string, object?>>> SyncLogAsync(ReportFilter f)
    {
        var (from, to) = Range(f);
        return _db.ProcRowsAsync("dbo.Rpt_SyncLog",
            ("@Max", MaxRows), ("@VesselID", f.VesselId), ("@Status", Text(f.Status)), ("@From", from), ("@To", to));
    }

    private Task<List<Dictionary<string, object?>>> SuggestionsAsync(ReportFilter f)
    {
        var (from, to) = Range(f);
        return _db.ProcRowsAsync("dbo.Rpt_Suggestions",
            ("@Max", MaxRows), ("@VesselID", f.VesselId), ("@Status", Text(f.Status)), ("@From", from), ("@To", to));
    }
}
