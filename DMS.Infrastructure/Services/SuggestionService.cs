using DMS.Application.DTO.Features;
using DMS.Application.Interface;
using DMS.Data.EF.Context;
using DMS.Data.EF.Features;
using DMS.Data.EF.Models.Features;
using Microsoft.EntityFrameworkCore;

namespace DMS.Infrastructure.Services;

/// <summary>
/// Feature 4: the suggestions workflow.
/// Submitted -> UnderReview -> NeedsInfo (answered on board, back to UnderReview) -> Accepted / Rejected -> Implemented -> Closed.
/// A vessel's suggestion keeps that vessel's VesselID, so the office's status changes and comments (SUG_Master, SUG_Log)
/// sync back to the ship; a suggestion raised at the office is office only (VesselID -2).
/// </summary>
public sealed class SuggestionService : ISuggestionService
{
    public static readonly string[] Statuses = { "Submitted", "UnderReview", "NeedsInfo", "Accepted", "Rejected", "Implemented", "Closed" };
    private static readonly string[] Priorities = { "Low", "Medium", "High" };
    private static readonly string[] ItemTypes = { "Manual", "Circular", "Alert", "Document" };

    /// <summary>The moves the office may make from each status.</summary>
    private static readonly Dictionary<string, string[]> Moves = new()
    {
        ["Submitted"] = new[] { "UnderReview", "NeedsInfo", "Accepted", "Rejected" },
        ["UnderReview"] = new[] { "NeedsInfo", "Accepted", "Rejected" },
        ["NeedsInfo"] = new[] { "UnderReview", "Accepted", "Rejected" },
        ["Accepted"] = new[] { "Implemented", "Rejected", "Closed" },
        ["Rejected"] = new[] { "UnderReview", "Closed" },
        ["Implemented"] = new[] { "Closed" },
        ["Closed"] = new[] { "UnderReview" },
    };

    private readonly GeneralContext _ctx;
    private readonly DmsContext _dms;
    private readonly CircularsContext _cir;
    private readonly OtherDocumentsContext _docs;
    private readonly SqlDb _db;

    public SuggestionService(GeneralContext ctx, DmsContext dms, CircularsContext cir, OtherDocumentsContext docs, SqlDb db)
    {
        _ctx = ctx;
        _dms = dms;
        _cir = cir;
        _docs = docs;
        _db = db;
    }

    private static readonly string[] ClosedStatuses = { "Rejected", "Implemented", "Closed" };

    /// <summary>Suggestions with the vessel name; ItemTitle is filled afterwards by <see cref="FillItemTitlesAsync"/>.</summary>
    private IQueryable<SuggestionDetail> Details(IQueryable<SugMaster> source)
        => from s in source
           join v in _ctx.VVessels on s.VesselID equals v.VesselID into vs
           from v in vs.DefaultIfEmpty()
           select new SuggestionDetail
           {
               SUG_MasterID = s.SUG_MasterID,
               VesselID = s.VesselID,
               VesselName = s.VesselID > 0 ? v.VslName : "Office",
               SuggestionNo = s.SuggestionNo,
               Title = s.Title,
               ItemType = s.ItemType,
               ItemID = s.ItemID,
               Priority = s.Priority,
               Status = s.Status,
               SubmittedBy = s.SubmittedBy,
               SubmittedByName = s.SubmittedByName,
               SubmittedRank = s.SubmittedRank,
               SubmittedOn = s.SubmittedOn,
               AssignedTo = s.AssignedTo,
               ModifiedOn = s.ModifiedOn,
               Description = s.Description,
               QuotedText = s.QuotedText,
               ImplementedItemType = s.ImplementedItemType,
               ImplementedItemID = s.ImplementedItemID,
           };

    public async Task<List<SuggestionListItem>> GetListAsync(string? status, int? vesselId, string? submittedBy)
    {
        var query = _ctx.SugMasters.AsNoTracking().Where(s => s.IsDeleted == 0);
        if (!string.IsNullOrWhiteSpace(status))
            query = status == "Open"
                ? query.Where(s => !ClosedStatuses.Contains(s.Status))
                : query.Where(s => s.Status == status);
        if (vesselId != null) query = query.Where(s => s.VesselID == vesselId);
        if (!string.IsNullOrWhiteSpace(submittedBy)) query = query.Where(s => s.SubmittedBy == submittedBy);

        var list = await Details(query.OrderByDescending(s => s.SubmittedOn).ThenByDescending(s => s.SUG_MasterID)).ToListAsync();
        await FillItemTitlesAsync(list);
        return list.Select(d => new SuggestionListItem
        {
            SUG_MasterID = d.SUG_MasterID, VesselID = d.VesselID, VesselName = d.VesselName, SuggestionNo = d.SuggestionNo,
            Title = d.Title, ItemType = d.ItemType, ItemID = d.ItemID, ItemTitle = d.ItemTitle, Priority = d.Priority,
            Status = d.Status, SubmittedBy = d.SubmittedBy, SubmittedByName = d.SubmittedByName, SubmittedRank = d.SubmittedRank,
            SubmittedOn = d.SubmittedOn, AssignedTo = d.AssignedTo, ModifiedOn = d.ModifiedOn,
        }).ToList();
    }

    public async Task<SuggestionDetail?> GetAsync(long id)
    {
        var detail = await Details(_ctx.SugMasters.AsNoTracking().Where(s => s.SUG_MasterID == id && s.IsDeleted == 0)).FirstOrDefaultAsync();
        if (detail == null) return null;
        await FillItemTitlesAsync(new[] { detail });
        detail.Log = await _ctx.SugLogs.AsNoTracking()
            .Where(l => l.SUG_MasterID == id)
            .OrderBy(l => l.LogDate).ThenBy(l => l.SUG_LogID)
            .Select(l => new SuggestionLog
            {
                SUG_LogID = l.SUG_LogID, Status = l.Status, Comment = l.Comment, UserID = l.UserID, UserName = l.UserName, LogDate = l.LogDate,
            })
            .ToListAsync();
        detail.Files = await _ctx.SugBlobs.AsNoTracking()
            .Where(b => b.SUG_MasterID == id)
            .OrderBy(b => b.SUG_BlobID)
            .Select(b => new FileItem
            {
                Id = b.SUG_BlobID, FileName = b.FileName ?? "", FileType = b.FileType, BlobSize = b.BlobSize, UploadedOn = b.AddedOn,
            })
            .ToListAsync();
        return detail;
    }

    /// <summary>The title of the manual, circular / alert or document a suggestion is about.</summary>
    private async Task FillItemTitlesAsync(IReadOnlyCollection<SuggestionListItem> items)
    {
        List<long> Ids(params string[] types) => items
            .Where(i => i.ItemID != null && types.Contains(i.ItemType)).Select(i => i.ItemID!.Value).Distinct().ToList();

        var manualIds = Ids("Manual");
        var manuals = manualIds.Count == 0 ? new Dictionary<long, string>() : (await _dms.DmManuals.AsNoTracking()
                .Where(m => manualIds.Contains(m.DM_ManualID))
                .Select(m => new { m.DM_ManualID, m.ManualNo, m.ManualName })
                .ToListAsync())
            .GroupBy(m => m.DM_ManualID).ToDictionary(g => g.Key, g => g.First().ManualNo + " " + (g.First().ManualName ?? ""));

        var circularIds = Ids("Circular", "Alert");
        var circulars = circularIds.Count == 0 ? new Dictionary<long, string>() : (await _cir.CIR_Master.AsNoTracking()
                .Where(c => circularIds.Contains(c.CIR_MasterID))
                .Select(c => new { c.CIR_MasterID, c.CIR_Number, c.Title })
                .ToListAsync())
            .GroupBy(c => c.CIR_MasterID).ToDictionary(g => g.Key, g => (g.First().CIR_Number ?? "") + " " + (g.First().Title ?? ""));

        var documentIds = Ids("Document");
        var documents = documentIds.Count == 0 ? new Dictionary<long, string>() : (await _docs.OtherDocumentMaster.AsNoTracking()
                .Where(d => documentIds.Contains(d.OtherDocumentMasterID))
                .Select(d => new { d.OtherDocumentMasterID, d.DocumentNumber, d.DocumentTitle })
                .ToListAsync())
            .GroupBy(d => d.OtherDocumentMasterID).ToDictionary(g => g.Key, g => g.First().DocumentNumber + " " + g.First().DocumentTitle);

        foreach (var item in items)
        {
            if (item.ItemID is not { } itemId) continue;
            item.ItemTitle = item.ItemType switch
            {
                "Manual" => manuals.GetValueOrDefault(itemId),
                "Circular" or "Alert" => circulars.GetValueOrDefault(itemId),
                "Document" => documents.GetValueOrDefault(itemId),
                _ => null,
            };
        }
    }

    public async Task<long> CreateAsync(NewSuggestion request, OfficeUser user)
    {
        var title = (request.Title ?? "").Trim();
        if (title.Length == 0) throw new ArgumentException("A title is required.");
        var itemType = ItemTypes.Contains(request.ItemType) ? request.ItemType : null;
        var priority = Priorities.Contains(request.Priority) ? request.Priority! : "Medium";
        var maxBytes = await MaxFileBytesAsync();
        if (request.Files.Any(f => f.Bytes.Length > maxBytes))
            throw new ArgumentException($"Each file must be {maxBytes / 1024 / 1024} MB or smaller.");
        foreach (var f in request.Files) FileRules.Check(f.FileName);

        var id = await _db.NextIdAsync("SUG_Master", vesselId: SqlDb.OfficeOnly);
        var logId = await _db.NextIdAsync("SUG_Log", vesselId: SqlDb.OfficeOnly);
        var firstBlob = request.Files.Count > 0 ? await _db.NextIdAsync("SUG_Blob", vesselId: SqlDb.OfficeOnly, count: request.Files.Count) : 0;
        var now = DateTime.UtcNow;

        _ctx.SugMasters.Add(new SugMaster
        {
            SUG_MasterID = id,
            Origin = SqlDb.Office,
            VesselID = SqlDb.OfficeOnly,
            SuggestionNo = $"SUG-{now:yyyy}-{id}",
            Title = Cut(title, 250)!,
            Description = request.Description,
            ItemType = itemType,
            ItemID = itemType == null ? null : request.ItemId,
            QuotedText = Cut(request.QuotedText, 1000),
            Priority = priority,
            Status = "Submitted",
            SubmittedBy = user.UserId,
            SubmittedByName = user.UserName,
            SubmittedRank = user.Role,
            SubmittedOn = now,
            IsDeleted = 0,
        });
        AddLog(logId, id, SqlDb.OfficeOnly, "Submitted", null, user);
        var blobId = firstBlob;
        foreach (var f in request.Files)
            _ctx.SugBlobs.Add(new SugBlob
            {
                SUG_BlobID = blobId++,
                Origin = SqlDb.Office,
                VesselID = SqlDb.OfficeOnly,
                SUG_MasterID = id,
                FileName = Cut(Path.GetFileName(f.FileName), 250),
                FileType = Cut(Path.GetExtension(f.FileName).TrimStart('.').ToLowerInvariant(), 50),
                BlobSize = f.Bytes.Length,
                BlobContents = f.Bytes,
                AddedOn = now,
            });
        await _ctx.SaveChangesAsync();   // one transaction
        return id;
    }

    public async Task<string?> ChangeStatusAsync(long id, SuggestionStatusChange request, OfficeUser user)
    {
        var current = await _ctx.SugMasters.FirstOrDefaultAsync(s => s.SUG_MasterID == id && s.IsDeleted == 0);
        if (current == null) return "Suggestion not found.";
        if (!Statuses.Contains(request.Status)) return "Unknown status.";
        if (request.Status == current.Status) return null;
        if (!Moves.TryGetValue(current.Status, out var allowed) || !allowed.Contains(request.Status))
            return $"A suggestion cannot move from {current.Status} to {request.Status}.";
        if (request.Status is "NeedsInfo" or "Rejected" && string.IsNullOrWhiteSpace(request.Comment))
            return request.Status == "NeedsInfo" ? "Say what information is needed." : "Give a reason for rejecting it.";

        var implType = request.Status == "Implemented" && ItemTypes.Contains(request.ImplementedItemType) ? request.ImplementedItemType : null;
        var logId = await _db.NextIdAsync("SUG_Log", vesselId: current.VesselID > 0 ? current.VesselID : -1);

        current.Status = request.Status;
        current.ModifiedOn = DateTime.UtcNow;
        if (request.Status == "UnderReview" && current.AssignedTo == null) current.AssignedTo = user.UserId;
        if (request.Status == "Implemented")
        {
            current.ImplementedItemType = implType;
            current.ImplementedItemID = implType == null ? null : request.ImplementedItemId;
        }
        AddLog(logId, id, current.VesselID, request.Status, request.Comment, user);
        await _ctx.SaveChangesAsync();   // one transaction
        return null;
    }

    public async Task AddCommentAsync(long id, string comment, OfficeUser user)
    {
        if (string.IsNullOrWhiteSpace(comment)) return;
        var suggestion = await _ctx.SugMasters.FirstOrDefaultAsync(s => s.SUG_MasterID == id && s.IsDeleted == 0)
                         ?? throw new KeyNotFoundException("Suggestion not found.");
        var logId = await _db.NextIdAsync("SUG_Log", vesselId: suggestion.VesselID > 0 ? suggestion.VesselID : -1);
        AddLog(logId, id, suggestion.VesselID, null, comment, user);
        suggestion.ModifiedOn = DateTime.UtcNow;
        await _ctx.SaveChangesAsync();
    }

    public async Task<FileContent?> GetFileAsync(long blobId)
    {
        var row = await _ctx.SugBlobs.AsNoTracking()
            .Where(b => b.SUG_BlobID == blobId)
            .Select(b => new { b.FileName, b.BlobContents })
            .FirstOrDefaultAsync();
        return row?.BlobContents == null ? null : new FileContent(row.BlobContents, ContentTypes.For(row.FileName), row.FileName ?? $"file-{blobId}");
    }

    /// <summary>Log rows carry the suggestion's VesselID, so a vessel's suggestion log syncs to that vessel.</summary>
    private void AddLog(long logId, long sugId, int vesselId, string? status, string? comment, OfficeUser user)
        => _ctx.SugLogs.Add(new SugLog
        {
            SUG_LogID = logId,
            Origin = SqlDb.Office,
            VesselID = vesselId,
            SUG_MasterID = sugId,
            Status = status,
            Comment = Cut(comment, 2000),
            UserID = user.UserId,
            UserName = user.UserName,
            LogDate = DateTime.UtcNow,
        });

    private async Task<long> MaxFileBytesAsync()
        => (int.TryParse(await _db.SettingAsync("DM_Settings", "FLD_MaxFileSizeMB"), out var mb) && mb > 0 ? mb : 25) * 1024L * 1024L;

    private static string? Cut(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= max ? s.Trim() : s.Trim()[..max];
}

/// <summary>Content type from a file name, for downloads.</summary>
public static class ContentTypes
{
    public static string For(string? fileName) => Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".txt" => "text/plain",
        ".htm" or ".html" => "text/html",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };
}
