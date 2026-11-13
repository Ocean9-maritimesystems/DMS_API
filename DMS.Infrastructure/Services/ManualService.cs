using AutoMapper;
using DMS.Application.Interface;
using DMS.Data.EF;
using DMS.Data.EF.Constant;
using DMS.Data.EF.Context;
using DMS.Data.EF.Features;
using DMS.Application.DTO.Circulars;
using DMS.Application.DTO.Manuals;
using DMS.Data.EF.Models.Manuals;
using DMS.Data.EF.Query.Manuals;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DMS.Application;

public class ManualService : IManualService
{
    private readonly DmsContext _dbContext;
    private readonly IMediator _mediator;
    private readonly ManualGeneralQueries _queries;
    private readonly IMapper _mapper;

    public ManualService(DmsContext context, IMediator mediator, ManualGeneralQueries queries, IMapper mapper)
    {
        _dbContext = context;
        _mediator = mediator;
        _queries = queries;
         _mapper = mapper;
    }

    public async Task<List<Manual_TreeviewDto>> GetTreeViewManualListAsync(string userID)
    {
        var manualList = await _queries.GetDmManualTreeviewAsync(userID);

        if (manualList == null || !manualList.Any())
            throw new InvalidOperationException("No manuals found in the database.");

        return BuildTree(manualList.Select(m => (m, true)), keepOrphans: false);
    }

    /// <summary>Calling modes accepted from the links (dashboard and Manuals page). Anything else = All.</summary>
    private static readonly Dictionary<string, string> TreeModes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["new"] = "New", ["NewDocuments"] = "New",
        ["userfavorites"] = "Favourites", ["favorites"] = "Favourites", ["Favourites"] = "Favourites",
        ["vsltoack"] = "VslToAck",
        ["usertoack"] = "PendingMyAck", ["PendingMyAcknowledgement"] = "PendingMyAck",
        ["pendingapproval"] = "PendingMyApproval", ["PendingMyApproval"] = "PendingMyApproval",
        ["underreview"] = "PendingMyReview", ["PendingMyReview"] = "PendingMyReview",
    };

    public async Task<List<Manual_TreeviewDto>> GetTreeViewManualListAsync(string userID, string? calledMode)
    {
        if (string.IsNullOrWhiteSpace(calledMode) || !TreeModes.TryGetValue(calledMode.Trim(), out var mode))
            return await GetTreeViewManualListAsync(userID);

        // An empty result is normal here ("no favourites yet"), so no exception.
        var rows = await _queries.GetDmManualTreeviewFilteredAsync(userID, mode);
        return BuildTree(rows.Select(r => (r.ToTreeRow(), r.IsMatch == 1)), keepOrphans: true);
    }

    private static List<Manual_TreeviewDto> BuildTree(IEnumerable<(Manual_TreeviewEntity Row, bool IsMatch)> rows, bool keepOrphans)
    {
        var list = rows.ToList();
        var lookup = new Dictionary<long, Manual_TreeviewDto>();
        foreach (var (m, isMatch) in list)
        {
            lookup[m.DM_ManualID] = new Manual_TreeviewDto
            {
                Key = m.DM_ManualID,
                Label = m.ManualNo + " - " + m.ManualName,
                Selectable = isMatch,
                ClassName = isMatch ? null : "tree-node-context",
                data = new TreeviewDataDto
                {
                    DM_ManualID = m.DM_ManualID,
                    DM_ManualVersionID = m.DM_ManualVersionID,
                    ManualVersion = m.Version,
                    StatusString = m.StatusString,
                    Category = m.CategoryName,
                    LastUpdated = m.LastUpdated,
                    ParentID = m.ParentID,
                    ManualLevel = m.ManualLevel,
                    DM_StatusID = m.DM_StatusID,
                    IsActive = m.IsActive,
                    IsFavourite = m.IsFavourite,
                    IsMainTitle = m.IsMainTitle,
                    IsMatch = isMatch
                }
            };
        }

        var root = new List<Manual_TreeviewDto>();
        foreach (var (m, _) in list)
        {
            if (m.ParentID == -1)
            {
                root.Add(lookup[m.DM_ManualID]);
            }
            else if (lookup.TryGetValue(m.ParentID, out var parent))
            {
                parent.Children.Add(lookup[m.DM_ManualID]);
            }
            else if (keepOrphans)
            {
                // Filtered view: a match whose parent is missing (e.g. deleted) is still shown, at the top.
                root.Add(lookup[m.DM_ManualID]);
            }
            // Full tree: same as before, a node whose parent is not in the list is left out.
        }
        return root;
    }

    public async Task<List<Manual_FilterDto>> GetManualListBasedOnCalledModeAsync(string calledMode, string userID)
    {
        var manualList = await _queries.GetDmManualTreeviewAsync(userID);

        if (manualList == null || !manualList.Any())
            throw new InvalidOperationException("No manuals found in the database.");

        var result = manualList.Select(m => new Manual_FilterDto
        {
            Key = m.DM_ManualID,
            Label = m.ManualNo + " - " + m.ManualName,
            data = new TreeviewDataDto
            {
                DM_ManualID = m.DM_ManualID,
                DM_ManualVersionID = m.DM_ManualVersionID,
                ManualVersion = m.Version,
                StatusString = m.StatusString,
                Category = m.CategoryName,
                LastUpdated = m.LastUpdated,
                ParentID = m.ParentID,
                ManualLevel = m.ManualLevel,
                DM_StatusID = m.DM_StatusID,
                IsActive = m.IsActive,
                IsFavourite = m.IsFavourite,
                IsMainTitle = m.IsMainTitle
            }
        }).ToList();

        return result;
    }

    public async Task<string?> GetManualContent(long dm_ManualID, bool includeSubManuals)
    {
        return await _queries.GetDmManualContentAsync(dm_ManualID, includeSubManuals);
    }

    public async Task<List<ManualVersionDetailsDto>> GetManualVersionDetailsAsync(long dm_ManualID)
    {
        return await _queries.GetDmManualVersionList(dm_ManualID);
    }
    
    public async Task<ManualCount_User?> GetUserManualCountAsync(string userID)
    {
        return await _queries.GetUserManualCount(userID);
    }

    public async Task<ManualCount_Dashboard?> GetDashboardManualCountsAsync(string userID)
    {
        return await _queries.GetDashboardManualCounts(userID);
    }

    public async Task<CompareVersionDetailsDto> GetCompareVersionDetailsAsync(long dm_ManualID, long manualVersionIDToCompare)
    {
        var compareVersionDetails = new CompareVersionDetailsDto();

        // Get Latest Version
        var latestVersions = await _dbContext.DmManualVersions
                                    .Where(v => v.IsActive == 1 && v.DM_ManualID == dm_ManualID)
                                    .GroupBy(v => v.DM_ManualID)
                                    .Select(g => g.OrderByDescending(v => v.Version).First().DM_ManualVersionID).FirstOrDefaultAsync();

        // Get Latest Version Content
        var currentVersionContent = await (from manualVersion in _dbContext.DmManualVersions
                                           where manualVersion.DM_ManualVersionID == latestVersions
                                           select new
                                           {
                                               current_DM_VersionID = manualVersion.DM_ManualVersionID,
                                               current_Version = manualVersion.Version,
                                               current_Content = manualVersion.TextContents,
                                           }).FirstOrDefaultAsync();

        if (currentVersionContent != null)
        {
            compareVersionDetails.current_DmManualVersionID = currentVersionContent.current_DM_VersionID;
            compareVersionDetails.current_Version = currentVersionContent.current_Version ?? "";
            compareVersionDetails.current_ManualContent = currentVersionContent.current_Content ?? "";
        }

        // Get Compare version Content
        var compareVersionContent = await (from manualVersion in _dbContext.DmManualVersions
                                           where manualVersion.DM_ManualVersionID == manualVersionIDToCompare
                                           select new
                                           {
                                               compare_DM_VersionID = manualVersion.DM_ManualVersionID,
                                               compare_Version = manualVersion.Version,
                                               compare_Content = manualVersion.TextContents,
                                           }).FirstOrDefaultAsync();

        if (compareVersionContent != null)
        {
            compareVersionDetails.compare_DmManualVersionID = compareVersionContent.compare_DM_VersionID;
            compareVersionDetails.compare_Version = compareVersionContent.compare_Version ?? "";
            compareVersionDetails.compare_ManualContent = compareVersionContent.compare_Content ?? "";
        }

        return compareVersionDetails;
    }

    public async Task<List<ManualCategoryDto>> GetManualCategory()
    {
        var categoryList = await _dbContext.DmCategories
                                   .Where(x => x.IsDisabled == 0)
                                   .Select(x => new ManualCategoryDto
                                   {
                                       DM_CategoryID = x.DM_CategoryID,
                                       CategoryName = x.CategoryName
                                   }).ToListAsync();
        return categoryList;
    }

    /// <summary>
    /// The next manual code for a category, like the circular number: {CategoryCode}-{sequence:000}, one more than the
    /// highest sequence already used with that prefix (deleted manuals count, so a code is never given out twice).
    /// Empty when the category has no code or the code would not fit DM_Manual.ManualCode (20 characters).
    /// </summary>
    public async Task<string> GetNextManualCodeAsync(long categoryId)
    {
        var categoryCode = await _dbContext.DmCategories
                                   .AsNoTracking()
                                   .Where(c => c.DM_CategoryID == categoryId)
                                   .Select(c => c.CategoryCode)
                                   .FirstOrDefaultAsync();
        categoryCode = (categoryCode ?? string.Empty).Trim();
        if (categoryCode.Length == 0) return string.Empty;

        var prefix = $"{categoryCode}-";
        var codes = await _dbContext.DmManuals
                            .AsNoTracking()
                            .Where(m => m.ManualCode != null && m.ManualCode.StartsWith(prefix))
                            .Select(m => m.ManualCode!)
                            .ToListAsync();

        var maxSeq = 0;
        foreach (var code in codes)
        {
            var rest = code.Trim();
            if (!rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(rest[prefix.Length..], out var seq)) maxSeq = Math.Max(maxSeq, seq);
        }

        var next = $"{prefix}{maxSeq + 1:D3}";
        return next.Length <= 20 ? next : string.Empty;
    }

    /// <summary>
    /// The approval steps of the manual's flow (DM_ApprovalAndMailSetup, statuses between Draft and Released, in approval
    /// order), each marked done / current / pending from the manual's status, with who approved it and when for the
    /// steps done (DM_ManualApprovedList for this version, not voided by a send back, matched in the order approved).
    /// </summary>
    public async Task<ManualApprovalProgressDto> GetApprovalProgressAsync(long dm_ManualID, long dm_ManualVersionID)
    {
        var manual = await _dbContext.DmManuals.AsNoTracking()
            .Where(m => m.DM_ManualID == dm_ManualID)
            .Select(m => new { m.DmStatusId, m.Approval_FlowID, m.IsApprovalRequired })
            .FirstOrDefaultAsync();
        if (manual == null) throw new KeyNotFoundException($"Manual {dm_ManualID} does not exist.");

        var result = new ManualApprovalProgressDto
        {
            StatusId = manual.DmStatusId,
            ApprovalRequired = (manual.IsApprovalRequired ?? 0) == 1 && (manual.Approval_FlowID ?? 0) > 0,
        };
        if (!result.ApprovalRequired) return result;

        var flowId = manual.Approval_FlowID!.Value;
        var setup = await _dbContext.DmApprovalAndMailSetups.AsNoTracking()
            .Where(s => s.FlowId == flowId && s.DmStatusId > DMStatusConstant.STATUS_DRAFT && s.DmStatusId < DMStatusConstant.STATUS_RELEASE)
            .Select(s => new { StatusId = s.DmStatusId!.Value, s.ApprovalOrder, s.UserRole, s.HQDept })
            .ToListAsync();
        var names = await _dbContext.DmStatuses.AsNoTracking()
            .Select(s => new { s.DmStatusId, s.StatusName })
            .ToDictionaryAsync(s => s.DmStatusId, s => s.StatusName);
        var approvals = await _dbContext.DmManualApprovedLists.AsNoTracking()
            .Where(a => a.DM_ManualID == dm_ManualID && a.DM_ManualVersionID == dm_ManualVersionID && (a.IsVoid ?? 0) == 0)
            .OrderBy(a => a.ApprovedDate)
            .Select(a => new { a.UserName, a.UserId, a.ApprovedDate, a.Comments })
            .ToListAsync();

        var steps = setup
            .GroupBy(s => s.StatusId)
            .Select(g => g.OrderBy(s => s.ApprovalOrder ?? int.MaxValue).First())
            .OrderBy(s => s.ApprovalOrder ?? int.MaxValue).ThenBy(s => s.StatusId)
            .ToList();
        var released = manual.DmStatusId >= DMStatusConstant.STATUS_RELEASE;
        var currentIndex = steps.FindIndex(s => s.StatusId == manual.DmStatusId);

        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var state = released || (currentIndex >= 0 && i < currentIndex) ? "done" : i == currentIndex ? "current" : "pending";
            var step = new ManualApprovalStepDto
            {
                StatusId = s.StatusId,
                Name = names.TryGetValue(s.StatusId, out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : $"Step {i + 1}",
                Role = s.UserRole,
                Dept = s.HQDept,
                State = state,
            };
            if (state == "done" && i < approvals.Count)
            {
                var a = approvals[i];
                step.ApprovedBy = string.IsNullOrWhiteSpace(a.UserName) ? a.UserId : a.UserName;
                step.ApprovedOn = a.ApprovedDate;
                step.Comments = a.Comments;
            }
            result.Steps.Add(step);
        }
        return result;
    }

    public async Task<List<ManualApprovalFlowListDto>> GetApprovalFlowList()
    {
        var approvalList = await _dbContext.DmApprovalAndMailSetups
            .Select(x => x.FlowId)
            .Distinct()
            .Select(flowId => new ManualApprovalFlowListDto
            {
                FlowID = flowId,
                FlowDesc = "Flow - " + flowId,
            })
            .ToListAsync();

        return approvalList;
    }

    public async Task<List<ManualApprovalRoleListDto>> GetApprovalDetailsForFlowID(int flowID)
    {
        var approvalList = await _queries.GetManualApprovalFlowDetails(flowID);

        return approvalList;
    }

    public async Task<List<DmManualAttachment>> GetManualAttachmentListAsync(long dm_ManualID)
    {
        // The list without the file contents (all of a manual's images could be many MB); a file is read on its own.
        return await _dbContext.DmManualAttachments.AsNoTracking()
            .Where(a => a.DM_ManualID == dm_ManualID)
            .Select(a => new DmManualAttachment
            {
                DmManualAttachmentsId = a.DmManualAttachmentsId,
                Origin = a.Origin,
                VesselID = a.VesselID,
                DM_ManualID = a.DM_ManualID,
                ImageRefNo = a.ImageRefNo,
                BlobSize = a.BlobSize,
                FileType = a.FileType,
                AddedOn = a.AddedOn,
                IsDeleted = a.IsDeleted,
            })
            .ToListAsync();
    }

    public async Task<ManualDetails> GetManualDetailsByVersionAsync(long dm_ManualVersionID)
    {
        var data = await (from manualVersion in _dbContext.VwDmManualVersions
                          join category in _dbContext.DmCategories
                               on manualVersion.DM_CategoryID equals category.DM_CategoryID
                          join status in _dbContext.DmStatuses
                               on manualVersion.DmStatusId equals status.DmStatusId
                          where manualVersion.DM_ManualVersionID == dm_ManualVersionID
                          select new ManualDetails()
                          {
                              DM_ManualID = manualVersion.DM_ManualID,
                              DM_ManualVersionID = manualVersion.DM_ManualVersionID,
                              VesselID = manualVersion.VesselID,
                              DM_CategoryID = manualVersion.DM_CategoryID,
                              DM_StatusID = manualVersion.DmStatusId,
                              CategoryName = category.CategoryName,
                              Version = manualVersion.Version,
                              ManualNo = manualVersion.ManualNo,
                              ManualCode = manualVersion.ManualCode,
                              ManualName = manualVersion.ManualName,
                              TextContents = manualVersion.TextContents,
                              StatusString = status.StatusName,
                              IsMainTitle = manualVersion.IsMainTitle,
                              IsAcknowledgementRequired = manualVersion.IsAcknowledgementRequired,
                              CanExport = manualVersion.CanExport,
                              IsApprovalRequired = manualVersion.IsApprovalRequired,
                              Approval_FlowID = manualVersion.Approval_FlowID,
                              Comments  = manualVersion.Comments,
                              CreatedBy = manualVersion.CreatedBy,
                              CreatedDate = manualVersion.CreatedDate,
                              LastModifiedBy = manualVersion.LastModifiedBy,
                              LastModifiedDate = manualVersion.LastModifiedDate,
                              PublishedBy = manualVersion.PublishedBy,
                              PublishedDate = manualVersion.PublishedDate,
                              ReleasedBy = manualVersion.ReleasedBy,
                              ReleasedDate = manualVersion.ReleasedDate,
                          }).FirstOrDefaultAsync();

        if (data == null)
            throw new KeyNotFoundException("The manual provided does not exist.");

        return data;
    }

    public async Task<Manual_SimpleDetails> GetManualSimpleDetailsAsync(long dm_ManualID)
    {
        var latestVersions = await GetManualLatestVersionID(dm_ManualID);

        var data = await (from manualVersion in _dbContext.VwDmManualVersions
                          join status in _dbContext.DmStatuses
                               on manualVersion.DmStatusId equals status.DmStatusId
                          where manualVersion.DM_ManualVersionID == latestVersions
                          select new Manual_SimpleDetails()
                          {
                              Version = manualVersion.Version,
                              ManualCode = manualVersion.ManualCode,
                              ManualName = manualVersion.ManualName,
                              StatusString = status.StatusName,
                          }).FirstOrDefaultAsync();

        if (data == null)
            throw new KeyNotFoundException("The manual provided does not exist.");

        return data;
    }

    public async Task<long> GetManualLatestVersionID(long dm_ManualID)
    {
        var latestVersions = await _dbContext.DmManualVersions
                                        .Where(v => v.IsActive == 1 && v.DM_ManualID == dm_ManualID)
                                        .GroupBy(v => v.DM_ManualID)
                                        .Select(g => g.OrderByDescending(v => v.DM_ManualVersionID).First().DM_ManualVersionID)
                                        .FirstOrDefaultAsync();
        return latestVersions;
    }

    public async Task<List<ManualDetails>> GetUserManualListBasedOnCalledModeAsync(string userID, string calledMode)
    {
        var data = await _queries.GetUserActionManualList(userID, calledMode);

        return data;
    }

    public async Task<List<ManualDetails>> GetUserFavouiteManualListAsync(string userID)
    {
        var data = await (from favourites in _dbContext.DmManualUserFavourites
                          join manualVersion in _dbContext.VwDmManualVersions
                               on favourites.DM_ManualID equals manualVersion.DM_ManualID
                          join status in _dbContext.DmStatuses
                               on manualVersion.DmStatusId equals status.DmStatusId
                          join categories in _dbContext.DmCategories
                               on manualVersion.DM_CategoryID equals categories.DM_CategoryID
                          where favourites.UserId == userID && (favourites.IsDisabled ?? 0) == 0
                          select new ManualDetails()
                          {
                              DM_ManualID = manualVersion.DM_ManualID,
                              DM_ManualVersionID = manualVersion.DM_ManualVersionID,
                              CategoryName = categories.CategoryName,
                              ManualCode = manualVersion.ManualCode,
                              ManualName = manualVersion.ManualName,
                              Version = manualVersion.Version,
                              StatusString = status.StatusName,
                              IsMainTitle = manualVersion.IsMainTitle ?? 0,
                              IsAcknowledgementRequired = manualVersion.IsAcknowledgementRequired,
                              CanExport = manualVersion.CanExport,
                              IsApprovalRequired = manualVersion.IsApprovalRequired,
                              Comments = manualVersion.Comments,
                              CreatedBy = manualVersion.CreatedBy,
                              CreatedDate = manualVersion.CreatedDate,
                              ReleasedBy = manualVersion.ReleasedBy,
                              ReleasedDate = manualVersion.ReleasedDate,
                              PublishedBy = manualVersion.PublishedBy,
                              PublishedDate = manualVersion.PublishedDate,
                              LastModifiedBy = manualVersion.LastModifiedBy,
                              LastModifiedDate = manualVersion.LastModifiedDate,
                          }).ToListAsync();

        return data;
    }

    public async Task<AssignManualRightsDto> GetManualRightsUsersAsync()
    {
        var vessels = await _queries.GetVesselList();
        var vslRanks = await _queries.GetVslRankList();
        var hqUsers = await _queries.GetHQUserList();

        return new AssignManualRightsDto
        {
            VesselList = vessels,
            VslRankList = vslRanks,
            HqUserList = hqUsers
        };
    }

    public async Task<ManualRightsDto> GetDmManualAssignedRightsAsync(long dm_ManualID)
    {
        var data = await (from manual in _dbContext.DmManuals
                          where manual.DM_ManualID == dm_ManualID
                          select new ManualRightsDto()
                          {
                              DM_ManualID = manual.DM_ManualID,
                              VesselIdList = _dbContext.DmManualToAckVsls
                                  .Where(r => r.DM_ManualID == manual.DM_ManualID && r.IsActive == 1)
                                  .Select(r => r.VesselID)
                                  .Distinct()
                                  .ToList(),
                              VesselRankList = _dbContext.DmManualToAckVsls
                                  .Where(r => r.DM_ManualID == manual.DM_ManualID && r.IsActive == 1)
                                  .Select(r => r.UserRank)
                                  .Distinct()
                                  .ToList(),
                              HQ_UsersIDList = _dbContext.DmManualToAckHqs
                                  .Where(r => r.DM_ManualID == manual.DM_ManualID && r.IsActive == 1)
                                  .Select(r => r.HqUsersId)
                                  .Distinct()
                                  .ToList(),
                          }).FirstOrDefaultAsync();

        if (data == null)
            throw new KeyNotFoundException("The manual provided does not exist.");

        return data;
    }

    public async Task<List<ManualUserAckList>> GetManualUserAckListAsync(long dm_ManualID)
    {
        var data = await _queries.GetUserAckList(dm_ManualID);

        return data;
    }

    public async Task<List<ManualNotificationDto>> GetManualNotificationListAsync(int limit, string userID)
    {
        var data = await _queries.GetNotificationList(userID, limit);

        var notificationList = _mapper.Map<List<ManualNotificationDto>>(data);

        // Read/unread is kept per user (Notification_Read); before that table exists, the shared flag is shown.
        if (await NotificationReads.IsInstalledAsync(_dbContext))
        {
            var read = await NotificationReads.ReadIdsAsync(_dbContext, NotificationReads.Manual, userID);
            foreach (var n in notificationList)
                n.IsRead = read.Contains(n.ManualNotificationId) ? 1 : 0;
        }

        return notificationList;
    }

    public async Task<List<ManualLogDto>> GetManualLogsAsync(long dm_ManualID)
    {
        var logs = await _dbContext.DmManualLogs
                            .Where(l => l.DM_ManualID == dm_ManualID)
                            .OrderByDescending(l => l.Logdate)
                            .Select(l => new ManualLogDto
                            {
                                DM_ManualLogID = l.DmManualLogId,
                                DM_ManualID = l.DM_ManualID,
                                DM_ManualVersionID = l.DM_ManualVersionID,
                                Logdate = l.Logdate,
                                UserId = l.UserName,
                                UserName = l.UserName,
                                Activity = l.Activity,
                                Remarks = l.Remarks,
                            })
                            .ToListAsync();
        return logs;
    }


    public async Task UpdateManualRights(ManualRightsDto request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        await _mediator.Send(new UpdateManualRightsCommand(request));
    }

    public async Task<CreateManualResultDto> AddDmManualAsync(CreateManualDto manual)
    {
        if (manual == null) throw new ArgumentNullException(nameof(manual));

        var result = await _mediator.Send(new CreateManualCommand(manual));

        return result;
    }

    public async Task<CreateManualResultDto> UpdateDmManualAsync(EditManualDto request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        var result = await _mediator.Send(new EditManualCommand(request));

        return result;
    }

    public async Task<bool> ApproveManualAsync(ManualApproveDto request, bool isToSendBack)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        await _mediator.Send(new ApproveManualCommand(request, isToSendBack));

        return true;
    }

    /// <summary>Submits a draft: released straight away, or sent to the first approver when the manual needs approval.</summary>
    public async Task<bool> PublishManualAsync(ManualApproveDto request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        return await _mediator.Send(new PublishManualCommand(new PublishManualDto
        {
            DM_ManualID = request.DM_ManualID,
            DM_ManualVersionID = request.DM_ManualVersionID,
            ManualName = request.ManualName,
            UserID = request.UserID,
            UserRole = request.UserRole,
            Comments = request.Comments,
        }));
    }

    public async Task DeleteDmManualAsync(long dm_ManualID, string userID)
    {
        await _mediator.Send(new DeleteManualCommand(new DeleteManualDto { DM_ManualID = dm_ManualID, UserID = userID }));
    }

    /// <summary>Removes the manual's newest version (a draft not yet released); the version before it becomes current again.</summary>
    public async Task DeleteDmManualVersionAsync(long dm_ManualID, string userID)
    {
        await _mediator.Send(new DeleteManualVersionCommand(new DeleteManualVersionDto
        {
            DM_ManualID = dm_ManualID,
            DM_ManualVersionID = 0, // the newest version is looked up by the handler
            UserID = userID,
        }));
    }

    public async Task AddToFavouriteAsync(long dm_ManualID, string userID)
    {
        // Added again after being removed: the removed row (IsDisabled = 1) is switched back on instead of adding another.
        var removed = await _dbContext.DmManualUserFavourites
            .Where(f => f.DM_ManualID == dm_ManualID && f.UserId == userID)
            .ToListAsync();
        if (removed.Any(f => (f.IsDisabled ?? 0) == 0)) return;
        if (removed.Count > 0)
        {
            removed[0].IsDisabled = 0;
            await _dbContext.SaveChangesAsync();
            return;
        }
        {
            var nextDM_ManualUserFavouriteID = await _dbContext.NextIdAsync<DmManualUserFavourite>();

            var userFavourite = new DmManualUserFavourite
            {
                DM_ManualUserFavouriteID = nextDM_ManualUserFavouriteID,
                Origin = "HQ",
                VesselID = -1,
                DM_ManualID = dm_ManualID,
                UserId = userID,
                IsDisabled = 0
            };
            await _dbContext.DmManualUserFavourites.AddAsync(userFavourite);

            await _dbContext.SaveChangesAsync();

            await _queries.AddNotification(dm_ManualID, -1, "", ManualNotificationType.FAVOURITE, "Manual added as Favourite for user - " + userID, "");
        }
    }

    /// <summary>
    /// Marks the user's favourite for the manual as removed (IsDisabled = 1); the row is kept. Everything that reads
    /// favourites leaves out IsDisabled = 1 rows: the list below, and the tree / counts / "Favourites" filter procedures
    /// through VW_DM_ManualUserFavourite_Active (Database\036_Favourites_ExcludeDisabled.sql).
    /// </summary>
    public async Task RemoveFromFavouriteAsync(long dm_ManualID, string userID)
    {
        await _dbContext.DmManualUserFavourites
            .Where(f => f.DM_ManualID == dm_ManualID && f.UserId == userID && (f.IsDisabled ?? 0) == 0)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.IsDisabled, 1));
    }

    public async Task<bool> MarkNotificationReadAsync(long notificationID, string userID)
    {
        var notification = await _dbContext.DM_ManualNotification
                                .FirstOrDefaultAsync(n => n.DM_ManualNotificationId == notificationID);
        if (notification == null) return false;

        if (await NotificationReads.IsInstalledAsync(_dbContext))
        {
            // Read for this user only; others still see it as new.
            await NotificationReads.MarkReadAsync(_dbContext, NotificationReads.Manual, notificationID, userID);
            return true;
        }

        notification.IsRead = 1;
        await _dbContext.SaveChangesAsync();
        return true;
    }

    public async Task<List<ManualVersionHistoryDto>> GetManualVersionHistoryAsync(long dm_ManualID)
    {
        return await _dbContext.DmManualVersions
            .AsNoTracking()
            .Where(v => v.DM_ManualID == dm_ManualID && (v.IsDisabled == null || v.IsDisabled == 0))
            .OrderByDescending(v => v.CreatedDate)
            .Select(v => new ManualVersionHistoryDto
            {
                DM_ManualVersionID = v.DM_ManualVersionID,
                VersionNumber = v.Version,
                CreatedBy = v.CreatedBy,
                CreatedDate = v.CreatedDate,
                PublishedBy = v.PublishedBy,
                PublishedDate = v.PublishedDate,
                ReleasedBy = v.ReleasedBy,
                ReleasedDate = v.ReleasedDate,
                IsActive = v.IsActive == 1
            })
            .ToListAsync();
    }
}
