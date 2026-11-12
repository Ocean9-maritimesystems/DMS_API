using AutoMapper;
using DMS.Application.DTO.Circulars;
using DMS.Application.DTO.Features;
using DMS.Application.Interface;
using DMS.Data.EF;
using DMS.Data.EF.Constant;
using DMS.Data.EF.Context;
using DMS.Data.EF.Features;
using DMS.Data.EF.Models.Circulars;
using DMS.Data.EF.Models.Features;
using DMS.Data.EF.Query.Circulars;
using DMS.Infrastructure.Query.Circulars;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Threading;
using System.Data;

namespace DMS.Application;

public class CIRService : ICIRService
{
    private static readonly string[] LinkTypes = { "Amends", "Supersedes", "RefersTo" };

    private readonly CircularsContext _dbContext;
    private readonly DmsContext _dms;
    private readonly IMediator _mediator;
    private readonly CIRGeneralQueries _queries;
    private readonly IMapper _mapper;
    private readonly AttachmentStorage _storage;

    public CIRService(CircularsContext context, DmsContext dms, IMediator mediator, CIRGeneralQueries queries, IMapper mapper, AttachmentStorage storage)
    {
        _dms = dms;
        _storage = storage;
        _dbContext = context;
        _mediator = mediator;
        _queries = queries;
        _mapper = mapper;
    }

    public async Task<List<Circulars_TreeviewDto>> GetTreeViewCircularsListAsync(string userID, string circularType)
    {
        var cirList = await _queries.GetCIRTreeviewAsync(userID, circularType);

        if (cirList == null || !cirList.Any())
            throw new InvalidOperationException("No cir found in the database.");

        var uniqueList = cirList
           .GroupBy(m => new { m.CIR_MasterID, m.CIR_CategoryID })
           .Select(g => g.First())
           .ToList();

        var lookup = uniqueList.ToDictionary(
            m => $"{m.CIR_CategoryID}-{m.CIR_MasterID}",
            m => new Circulars_TreeviewDto
            {
                Key = $"{m.CIR_CategoryID}-{m.CIR_MasterID}",
                Label = (m.CIRLevel == 0 ? m.Category ?? string.Empty : m.CIR_Number + " - " + m.Title),
                data = new CircularsTreeviewDataDto
                {
                    CIR_MasterID = m.CIR_MasterID,
                    CIR_BlobID = m.CIR_BlobId,
                    CIR_Number = m.CIR_Number,
                    Title = m.Title,
                    StatusString = m.StatusString,
                    Category = m.Category,
                    DateIssued = m.DateIssued ?? string.Empty,
                    Priority = m.Priority,
                    ParentID = m.ParentID,
                    CIRLevel = m.CIRLevel,
                    Reference = m.Reference,
                    IsActive = m.IsActive,
                    IsFavourite = m.IsFavourite,
                    IsCategory = m.IsCategory,
                }
            });

        var root = new List<Circulars_TreeviewDto>();

        // Initialize children for each CIR
        foreach (var cir in uniqueList)
        {
            var currentKey = $"{cir.CIR_CategoryID}-{cir.CIR_MasterID}";

            if (cir.ParentID == -1)
            {
                if (lookup.TryGetValue(currentKey, out var node))
                    root.Add(node);
                continue;
            }

            var parentEntry = uniqueList.FirstOrDefault(u => u.CIR_CategoryID == cir.ParentID);
            if (parentEntry != null)
            {
                var parentKey = $"{parentEntry.CIR_CategoryID}-{parentEntry.CIR_MasterID}";

                if (lookup.TryGetValue(parentKey, out var parent))
                {
                    parent.Children ??= new List<Circulars_TreeviewDto>();

                    if (lookup.TryGetValue(currentKey, out var child))
                        parent.Children.Add(child);
                }
            }

        }

        return root;
    }

    public async Task<List<CIRMasterList>> GetCIRMasterListAsync(string userID, long categoryId, string circularType)
    {
        var cirList = await _queries.GetCIRListAsync(userID, categoryId, circularType);

        // GetCIRMasterList does not return a reliable AttachmentCount, so compute it from CIR_Blob.
        // A blob row counts only when it still holds a file (removing an attachment on edit keeps the
        // row but clears FileName/BlobContents). BlobContents is only tested for NULL, never loaded.
        var masterIds = cirList
            .Where(c => c.IsCategory == 0 && c.CIR_MasterID > 0)
            .Select(c => c.CIR_MasterID)
            .Distinct()
            .ToList();

        if (masterIds.Count > 0)
        {
            var counts = await _dbContext.CIR_Blob
                .AsNoTracking()
                .Where(b => b.CIR_MasterID != null
                            && masterIds.Contains(b.CIR_MasterID.Value)
                            && (b.BlobContents != null || (b.FileName != null && b.FileName != "")))
                .GroupBy(b => b.CIR_MasterID!.Value)
                .Select(g => new { MasterId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.MasterId, x => x.Count);

            foreach (var item in cirList)
            {
                item.AttachmentCount = counts.TryGetValue(item.CIR_MasterID, out var count) ? count : 0;
            }
        }

        return cirList;
    }

    public async Task<List<CircularCategoryDto>> GetCategoriesAsync(bool isToIncludeAll, string? circularType = null)
    {
        var query = _dbContext.CIR_Category
            .Where(c =>
                (c.isActive ?? 0) == 1 &&
                c.CircularType != null &&
                c.CircularType != string.Empty);

        if (!string.IsNullOrWhiteSpace(circularType))
        {
            var normalizedCircularType = circularType.Trim();

            query = query.Where(c =>
                c.CircularType == normalizedCircularType);
        }

        var categories = await query
            .OrderBy(c => c.Sortorder)
            .ThenBy(c => c.Category)
            .Select(c => new CircularCategoryDto
            {
                CIR_CategoryID = c.CIR_CategoryID,
                Category = c.Category ?? string.Empty
            })
            .ToListAsync();

        if (isToIncludeAll &&
            !categories.Any(c => c.CIR_CategoryID == -1))
        {
            categories.Insert(0, new CircularCategoryDto
            {
                CIR_CategoryID = -1,
                Category = "All Categories"
            });
        }

        return categories;
    }

    public async Task<List<CircularsChart>> GetCIR_CategoryChartDataAsync(string circularType)
    {
        var data = await _queries.GetCIRCategoryBasedChartDataAsync(circularType);
        return data;
    }

    public async Task<List<CIRRecentList>> GetRecentCircularsAsync(string circularType, int limit = 10)
    {
        var data = await _queries.GetRecentCircularsAsync(circularType, limit);
        return data;
    }

    public async Task<CircularCount_Dashboard> GetDashboardCircularCountsAsync()
    {
        var counts = await _queries.GetCIRDashboardCountsAsync();
        return counts;
    }

    public async Task<List<CircularsNotificationDto>> GetCIRNotificationListAsync(int limit, string userID)
    {
        var data = await _queries.GetNotificationList(userID, limit);

        var notificationList = _mapper.Map<List<CircularsNotificationDto>>(data);

        // Read/unread is kept per user (Notification_Read); before that table exists, the shared flag is shown.
        if (await NotificationReads.IsInstalledAsync(_dbContext))
        {
            var read = await NotificationReads.ReadIdsAsync(_dbContext, NotificationReads.Circular, userID);
            foreach (var n in notificationList)
                n.IsRead = read.Contains(n.CIRNotificationId) ? 1 : 0;
        }

        return notificationList;
    }

    public async Task<List<CircularUserAckDto>> GetVesselAckListAsync(long cir_MasterID)
    {
        var data = await _queries.GetVesselAckListAsync(cir_MasterID);
        return data;
    }

    public async Task<GetNextCIRNumberResponseDto> GetNextCIRNumbersByCategoryAsync(long categoryId, long cir_MasterID)
    {
        // Validate category
        var cat = await _dbContext.CIR_Category
                   .Where(c => c.CIR_CategoryID == categoryId)
                   .Select(c => new { CategoryCode = c.CategoryCode ?? string.Empty, CircularType = c.CircularType ?? string.Empty })
                   .FirstOrDefaultAsync();

        if (cat == null)
            throw new KeyNotFoundException($"Category with ID {categoryId} not found.");

        var year = DateTime.UtcNow.Year;
        var circularTypePrefix = cat.CircularType == "Circulars" ? "CIR" : "ALT";
        var prefix = $"{circularTypePrefix}-{cat.CategoryCode}-{year}-";

        // Get existing numbers with the same prefix
        var existingNumbers = await _dbContext.CIR_Master
                                    .Where(m => m.CIR_CategoryID == categoryId
                                             && m.CIR_Number != null
                                             && Microsoft.EntityFrameworkCore.EF.Functions.Like(m.CIR_Number!, $"{prefix}%"))
                                    .Select(m => m.CIR_Number!)
                                    .ToListAsync();

        // Find max sequence used
        var maxSeq = 0;
        foreach (var num in existingNumbers)
        {
            var parts = num.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                var last = parts[^1];
                if (int.TryParse(last, out var seq))
                    maxSeq = Math.Max(maxSeq, seq);
            }
        }

        var nextSeq = maxSeq + 1;
        var cir_Number = $"{prefix}{nextSeq:D4}";

        var allowedUserToAck = await _dbContext.CIR_CategoryExt
                                       .Where(c => c.CIR_CategoryID == categoryId)
                                       .Select(c => c.AllowedUsersToAck)
                                       .FirstOrDefaultAsync();

        var cir_Master = await _dbContext.CIR_Master
                              .Where(m => m.CIR_MasterID == cir_MasterID)
                              .OrderByDescending(m => m.CIR_MasterID)
                              .FirstOrDefaultAsync();
        if (cir_Master != null)
        {
            cir_Master.CIR_Number = cir_Number;
            cir_Master.CIR_CategoryID = categoryId;
            _dbContext.CIR_Master.Update(cir_Master);
            await _dbContext.SaveChangesAsync();
        }

        return new GetNextCIRNumberResponseDto
        {
            CirNumber = cir_Number,
            AllowedUserToAck = allowedUserToAck ?? string.Empty
        };
    }

    public async Task<EditCircularDto> GetCIRDetailsAsync(long cirMasterId)
    {
        var query = from cir in _dbContext.VwCIRMasterBlob
                    where cir.CIR_MasterID == cirMasterId
                    select new
                    {
                        Circular = cir,
                    };

        var result = await query.FirstOrDefaultAsync();

        if (result?.Circular == null)
        {
            throw new KeyNotFoundException($"Circular with ID {cirMasterId} not found.");
        }

        var circularDto = _mapper.Map<EditCircularDto>(result.Circular);

        // An attachment kept in a local folder or the cloud is loaded from there (BlobTableSettings).
        if (circularDto.BlobContents == null && !string.IsNullOrEmpty(circularDto.FileName))
        {
            var blob = await _dbContext.CIR_Blob.AsNoTracking()
                .Where(b => b.CIR_BlobID == circularDto.CIR_BlobID)
                .Select(b => new { b.FileName, b.HQ_Storage_Option, b.HQ_Local_Folder, b.Cloud_ContainerName, b.Cloud_Directory })
                .FirstOrDefaultAsync();
            if (blob != null)
                circularDto.BlobContents = await _storage.ReadAsync(AttachmentStorage.CircularTable, new AttachmentRef(circularDto.CIR_BlobID,
                    blob.FileName, null, blob.HQ_Storage_Option, blob.HQ_Local_Folder, blob.Cloud_ContainerName, blob.Cloud_Directory));
        }
        var toAckRows = await _dbContext.CIR_ToAck_Vsl
                            .Where(x =>
                                x.CIR_MasterID == cirMasterId &&
                                x.IsActive == 1)
                            .ToListAsync();

        circularDto.VesselIdList = toAckRows
            .Select(x => x.VesselID)
            .Distinct()
            .ToList();

        circularDto.AllowedUserToAck = toAckRows
            .Select(x => x.UserRank)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        return circularDto;
    }

    public async Task<List<CIR_LogDto>> GetCircularLogsAsync(long cirMasterId)
    {
        var logs = await _dbContext.CIR_Log
                            .Where(l => l.CIR_MasterID == cirMasterId)
                            .OrderByDescending(l => l.LogDate)
                            .Select(l => new CIR_LogDto
                            {
                                CIR_LogID = l.CIR_LogID,
                                CIR_MasterID = l.CIR_MasterID,
                                Logdate = l.LogDate,
                                UserId = l.UserId,
                                UserName = l.UserName,
                                Activity = l.Activity,
                                Remarks = l.Remarks,
                            })
                            .ToListAsync();
        return logs;
    }


    public async Task<long> InsertCIR_MasterRecord(string circularType)
    {
        var cir_Master = new CIR_Master();
        cir_Master.CIR_MasterID = await _dbContext.NextIdAsync<CIR_Master>();
        cir_Master.Origin = "HQ";
        cir_Master.VesselID = -1;
        cir_Master.Status = "Temp";
        cir_Master.CIR_Number = "Temp";
        cir_Master.CIR_CategoryID = -1;
        cir_Master.CircularType = circularType;
        _dbContext.CIR_Master.Add(cir_Master);
        await _dbContext.SaveChangesAsync();

        return cir_Master.CIR_MasterID;
    }

    public async Task RemoveTempCIRAsync(long cir_MasterID)
    {
        var cir_Master = await _dbContext.CIR_Master
                              .Where(m => m.CIR_MasterID == cir_MasterID)
                              .OrderByDescending(m => m.CIR_MasterID)
                              .FirstOrDefaultAsync();
        if (cir_Master != null)
        {
            _dbContext.CIR_Master.Remove(cir_Master);
            await _dbContext.SaveChangesAsync();
        }
    }

    public async Task<long> CreateCircularAsync(CreateCircularDto createDto)
    {
        if (createDto is null)
            throw new ArgumentNullException(nameof(createDto));

        var result = await _mediator.Send(new CreateCircularsCommand(createDto));

        return result;
    }


    public async Task AddToFavouriteAsync(long cir_MasterID, string circularType, string userID)
    {
        if (!_dbContext.CIR_UserFavourite.Any(f => f.CIR_MasterID == cir_MasterID && f.UserId == userID))
        {
            var nextCIR_UserFavouriteID = await _dbContext.NextIdAsync<CIR_UserFavourite>();

            var userFavourite = new CIR_UserFavourite();
            userFavourite.CIR_UserFavouriteID = nextCIR_UserFavouriteID;
            userFavourite.Origin = "HQ";
            userFavourite.VesselID = -1;
            userFavourite.CIR_MasterID = cir_MasterID;
            userFavourite.UserId = userID;
            userFavourite.IsDisabled = 0;
            await _dbContext.CIR_UserFavourite.AddAsync(userFavourite);

            await _dbContext.SaveChangesAsync();

            await _queries.AddNotification(cir_MasterID, "", ManualNotificationType.FAVOURITE, circularType + " added as Favourite for user - " + userID, "");
        }
    }

    public async Task RemoveFromFavouriteAsync(long cir_MasterID, string userID)
    {
        var favourite = await _dbContext.CIR_UserFavourite
            .FirstOrDefaultAsync(f => f.CIR_MasterID == cir_MasterID && f.UserId == userID && (f.IsDisabled ?? 0) == 0);

        if (favourite == null)
            return;

        favourite.IsDisabled = 1;
        await _dbContext.SaveChangesAsync();
    }

    public async Task<long> UpdateCircularAsync(EditCircularDto editDto)
    {
        if (editDto is null)
            throw new ArgumentNullException(nameof(editDto));

        var result = await _mediator.Send(new UpdateCircularsCommand(editDto));

        return result;
    }

    public async Task<bool> MarkNotificationReadAsync(long notificationID, string userID)
    {
        var notification = await _dbContext.CIR_Notification
                                .FirstOrDefaultAsync(n => n.CIR_NotificationID == notificationID);
        if (notification == null) return false;

        if (await NotificationReads.IsInstalledAsync(_dbContext))
        {
            // Read for this user only; others still see it as new.
            await NotificationReads.MarkReadAsync(_dbContext, NotificationReads.Circular, notificationID, userID);
            return true;
        }

        notification.IsRead = 1;
        await _dbContext.SaveChangesAsync();
        return true;
    }

    /// <summary>Circular - manual links with the circular's number, title and status; the manual's number and name are added by <see cref="AddManualNamesAsync"/>.</summary>
    private IQueryable<CircularManualLink> Links(IQueryable<CirManualLink> links)
        => from l in links
           join c in _dbContext.CIR_Master on l.CIR_MasterID equals c.CIR_MasterID
           select new CircularManualLink
           {
               CIR_ManualLinkID = l.CIR_ManualLinkID,
               CIR_MasterID = l.CIR_MasterID,
               CircularNumber = c.CIR_Number,
               CircularTitle = c.Title,
               CircularType = c.CircularType,
               CircularStatus = c.Status,
               DM_ManualID = l.DM_ManualID,
               DM_ManualVersionID = l.DM_ManualVersionID,
               LinkType = l.LinkType,
               Remarks = l.Remarks,
               IsActive = l.IsActive == 1,
               ClosedOn = l.ClosedOn,
               CreatedOn = l.CreatedOn,
           };

    /// <summary>Adds ManualNo / ManualName and drops links whose manual is gone (the old query joined DM_Manual).</summary>
    private async Task<List<CircularManualLink>> AddManualNamesAsync(List<CircularManualLink> links)
    {
        var ids = links.Select(l => l.DM_ManualID).Distinct().ToList();
        if (ids.Count == 0) return links;
        var manuals = (await _dms.DmManuals.AsNoTracking()
                .Where(m => ids.Contains(m.DM_ManualID))
                .Select(m => new { m.DM_ManualID, m.ManualNo, m.ManualName })
                .ToListAsync())
            .GroupBy(m => m.DM_ManualID).ToDictionary(g => g.Key, g => g.First());
        var result = new List<CircularManualLink>(links.Count);
        foreach (var link in links)
        {
            if (!manuals.TryGetValue(link.DM_ManualID, out var manual)) continue;
            link.ManualNo = manual.ManualNo;
            link.ManualName = manual.ManualName;
            result.Add(link);
        }
        return result;
    }

    public async Task<CircularExtras?> GetCircularesExtraInfoAsync(long circularId)
    {
        var extras = await (
                from c in _dbContext.CIR_Master.AsNoTracking()
                where c.CIR_MasterID == circularId
                join s in _dbContext.CIR_Master on c.SupersededByID equals (long?)s.CIR_MasterID into ss
                from s in ss.DefaultIfEmpty()
                join cat in _dbContext.CIR_Category on c.CIR_CategoryID equals (long?)cat.CIR_CategoryID into cats
                from cat in cats.DefaultIfEmpty()
                select new CircularExtras
                {
                    CIR_MasterID = c.CIR_MasterID,
                    Status = c.Status,
                    AckDueDays = c.AckDueDays,
                    ValidUntil = c.ValidUntil,
                    SupersededByID = c.SupersededByID,
                    SupersededByNumber = s.CIR_Number,
                    RequiresValidUntil = cat != null && cat.RequiresValidUntil == 1,
                })
            .FirstOrDefaultAsync();
        if (extras == null) return null;

        var links = await Links(_dbContext.CirManualLinks.AsNoTracking().Where(l => l.CIR_MasterID == circularId)).ToListAsync();
        extras.Links = (await AddManualNamesAsync(links))
            .OrderByDescending(l => l.IsActive).ThenBy(l => l.ManualNo).ToList();
        return extras;
    }

    public async Task<string?> SaveCircularesExtraInfoAsync(long circularId, SaveCircularExtras request)
    {
        var circular = await _dbContext.CIR_Master.FirstOrDefaultAsync(m => m.CIR_MasterID == circularId);
        if (circular == null) return "Circular not found.";
        if (request.AckDueDays is < 0 or > 3650) return "Acknowledge-by days must be between 0 and 3650.";
        circular.AckDueDays = request.AckDueDays;
        circular.ValidUntil = request.ValidUntil?.Date;
        await _dbContext.SaveChangesAsync();
        return null;
    }

    public async Task<string?> AddManualLinkAsync(long circularId, AddManualLink request, OfficeUser user)
    {
        if (!LinkTypes.Contains(request.LinkType)) return "Link type must be Amends, Supersedes or RefersTo.";
        if (!await _dbContext.CIR_Master.AnyAsync(m => m.CIR_MasterID == circularId))
            return "Circular not found.";
        if (!await _dms.DmManuals.AnyAsync(m => m.DM_ManualID == request.ManualId && (m.IsDeleted ?? 0) != 1))
            return "Manual not found.";
        if (await _dbContext.CirManualLinks.AnyAsync(l => l.CIR_MasterID == circularId && l.DM_ManualID == request.ManualId && l.IsActive == 1))
            return "This manual is already linked.";

        _dbContext.CirManualLinks.Add(new CirManualLink
        {
            CIR_ManualLinkID = await _dbContext.NextIdAsync("CIR_ManualLink"),
            Origin = "HQ",
            VesselID = -1,
            CIR_MasterID = circularId,
            DM_ManualID = request.ManualId,
            DM_ManualVersionID = request.ManualVersionId,
            LinkType = request.LinkType,
            Remarks = Cut(request.Remarks, 500),
            IsActive = 1,
            CreatedBy = user.UserId,
            CreatedOn = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
        return null;
    }

    public Task RemoveManualLinkAsync(long linkId)
        => _dbContext.CirManualLinks.Where(l => l.CIR_ManualLinkID == linkId).ExecuteDeleteAsync();

    public async Task<string?> CancelCircularAsync(long circularId, CancelCircular request, OfficeUser user)
    {
        var current = await _dbContext.CIR_Master.FirstOrDefaultAsync(m => m.CIR_MasterID == circularId);
        if (current == null) return "Circular not found.";
        var status = current.Status;
        if (status == "Cancelled") return "This circular is already cancelled.";
        if (status == "Temp") return "Save the circular before cancelling it.";
        // A draft can be cancelled too: it was never sent, so the vessels are not told (TR_CIR_Master_Inbox, 018).
        if (request.SupersededById == circularId) return "A circular cannot supersede itself.";
        if (request.SupersededById != null && !await _dbContext.CIR_Master.AnyAsync(m => m.CIR_MasterID == request.SupersededById && (m.IsDeleted ?? 0) != 1))
            return "The superseding circular was not found.";
        var remarks = string.IsNullOrWhiteSpace(request.Reason) ? null : $"Cancelled by {user.UserName}: {request.Reason.Trim()}";

        current.Status = "Cancelled";
        current.SupersededByID = request.SupersededById;
        current.ModifiedBy = user.UserId;
        current.ModifiedDate = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(remarks))
        {
            var combined = (current.Remarks ?? string.Empty) + (string.IsNullOrEmpty(current.Remarks) ? string.Empty : "\r\n") + remarks;
            current.Remarks = combined.Length <= 4000 ? combined : combined.Substring(0, 4000);
        }
        await _dbContext.SaveChangesAsync();
        return null;
    }

    /// <summary>
    /// Releases a draft: Status Draft -> Active and ReleasedDate = now. From then on the circular is sent to the
    /// vessels with the sync, appears in their inbox (TR_CIR_Master_Inbox, 012) and in pending acknowledgements,
    /// and its manual links count as open amendments. A released circular can only be cancelled or superseded.
    /// </summary>
    public async Task<string?> ReleaseCircularAsync(long circularId, OfficeUser user)
    {
        var current = await _dbContext.CIR_Master.FirstOrDefaultAsync(m => m.CIR_MasterID == circularId);
        if (current == null || (current.IsDeleted ?? 0) == 1) return "Circular not found.";
        if (current.Status == "Temp") return "Save the circular before releasing it.";
        if (current.Status != "Draft") return $"Only a draft can be released; this one is {current.Status ?? "already released"}.";
        if (string.IsNullOrWhiteSpace(current.Title)) return "Give the circular a title before releasing it.";
        if (current.CIR_CategoryID == null) return "Choose a category before releasing the circular.";

        current.Status = "Active";
        current.ReleasedDate = DateTime.Now;
        current.ModifiedBy = user.UserId;
        current.ModifiedDate = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        var type = string.IsNullOrWhiteSpace(current.CircularType) ? "Circular" : current.CircularType;
        await _queries.AddToLog(circularId, $"{type} Released", $"{type}: {current.CIR_Number} released by {user.UserName}");
        return null;
    }

    /// <summary>
    /// "Save and release" from the Edit Circular form: the same save as EditCircular, then Draft -> Active.
    /// Everything that would stop the release is checked first, so a refused release leaves the circular unchanged.
    /// </summary>
    public async Task<string?> SaveAndReleaseCircularAsync(EditCircularDto editDto, OfficeUser user)
    {
        if (editDto is null) return "Circular payload is required.";

        var current = await _dbContext.CIR_Master.AsNoTracking()
            .Where(m => m.CIR_MasterID == editDto.CIR_MasterID)
            .Select(m => new { m.Status, m.IsDeleted })
            .FirstOrDefaultAsync();
        if (current == null || (current.IsDeleted ?? 0) == 1) return "Circular not found.";
        if (current.Status != "Draft")
            return $"Only a draft can be released; this one is {current.Status ?? "already released"}. Use Save instead.";
        if (string.IsNullOrWhiteSpace(editDto.Title)) return "Give the circular a title before releasing it.";
        if (editDto.CIR_CategoryID <= 0) return "Choose a category before releasing the circular.";

        // Saved as a draft first (the form's other changes), then released.
        editDto.Status = "Draft";
        editDto.ReleasedDate = null;
        await UpdateCircularAsync(editDto);
        return await ReleaseCircularAsync(editDto.CIR_MasterID, user);
    }

    public async Task<List<CircularManualLink>> GetForManualAsync(long manualId)
    {
        // Draft circulars are listed too (the page marks them "Draft"), so a link made while the circular is
        // still being written shows on the manual at once. Temp rows (unsaved) are left out.
        var links = await (
                from l in _dbContext.CirManualLinks.AsNoTracking()
                join c in _dbContext.CIR_Master on l.CIR_MasterID equals c.CIR_MasterID
                where l.DM_ManualID == manualId && (c.IsDeleted ?? 0) != 1 && (c.Status ?? "") != "Temp"
                orderby l.IsActive descending, (c.ReleasedDate ?? c.DateIssued) descending
                select new CircularManualLink
                {
                    CIR_ManualLinkID = l.CIR_ManualLinkID,
                    CIR_MasterID = l.CIR_MasterID,
                    CircularNumber = c.CIR_Number,
                    CircularTitle = c.Title,
                    CircularType = c.CircularType,
                    CircularStatus = c.Status,
                    DM_ManualID = l.DM_ManualID,
                    DM_ManualVersionID = l.DM_ManualVersionID,
                    LinkType = l.LinkType,
                    Remarks = l.Remarks,
                    IsActive = l.IsActive == 1,
                    ClosedOn = l.ClosedOn,
                    CreatedOn = l.CreatedOn,
                })
            .ToListAsync();
        return await AddManualNamesAsync(links);   // keeps the order
    }

    public async Task<int> CloseLinksForManualAsync(long manualId)
    {
        // Only links of circulars that were actually released: a draft's amendment cannot have been taken in yet.
        var now = DateTime.UtcNow;
        return await _dbContext.CirManualLinks
            .Where(l => l.DM_ManualID == manualId && l.IsActive == 1 && (l.LinkType == "Amends" || l.LinkType == "Supersedes")
                        && _dbContext.CIR_Master.Any(c => c.CIR_MasterID == l.CIR_MasterID
                                                          && (c.Status ?? "") != "Temp" && (c.Status ?? "") != "Draft"))
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.IsActive, 0).SetProperty(l => l.ClosedOn, now));
    }

    public Task<List<CircularCategorySetting>> GetCategoriesAsync()
        => _dbContext.CIR_Category.AsNoTracking()
            .Where(c => (c.isActive ?? 1) == 1)
            .OrderBy(c => c.CircularType).ThenBy(c => c.Sortorder).ThenBy(c => c.Category)
            .Select(c => new CircularCategorySetting
            {
                CIR_CategoryID = c.CIR_CategoryID,
                CircularType = c.CircularType,
                CategoryCode = c.CategoryCode,
                Category = c.Category,
                RequiresValidUntil = c.RequiresValidUntil == 1,
            })
            .ToListAsync();

    public Task SaveCategoryAsync(long categoryId, bool requiresValidUntil)
        => _dbContext.CIR_Category.Where(c => c.CIR_CategoryID == categoryId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.RequiresValidUntil, requiresValidUntil ? 1 : 0));

    public Task<ManualExtras?> GetManualAsync(long manualId)
        => _dms.DmManuals.AsNoTracking()
            .Where(m => m.DM_ManualID == manualId)
            .Select(m => new ManualExtras { DM_ManualID = m.DM_ManualID, AckDueDays = m.AckDueDays })
            .FirstOrDefaultAsync();

    public Task SaveManualAsync(long manualId, int? ackDueDays)
    {
        int? days = ackDueDays is null ? null : Math.Clamp(ackDueDays.Value, 0, 3650);
        return _dms.DmManuals.Where(m => m.DM_ManualID == manualId)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.AckDueDays, days));
    }

    private static string? Cut(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= max ? s.Trim() : s.Trim()[..max];


}
