using AutoMapper;
using DMS.Data.EF.Constant;
using DMS.Data.EF.Context;
using DMS.Application.DTO.Manuals;
using DMS.Data.EF.Models.Manuals;
using DMS.Data.EF.Query.Manuals;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Handler
{
    public class CreateManualCommandHandler : IRequestHandler<CreateManualCommand, CreateManualResultDto>
    {
        private readonly IMapper _mapper;
        private readonly ManualGeneralQueries _queries;
        private readonly DmsContext _dbContext;

        public CreateManualCommandHandler(DmsContext dbContext, IMapper mapper, ManualGeneralQueries queries)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext), "DmsContext cannot be null");
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper), "Mapper cannot be null");
            _queries = queries;
        }

        /// <summary>
        /// The number for a new manual. Top level: one more than the highest first part ("7.0" -> "8.0", "10.0" after "9.0").
        /// Below a manual: the siblings' prefix with one more than the highest last part ("1.9" -> "1.10" -> "1.11");
        /// the first one under a manual is its number + ".1" (as before). Deleted siblings count, so no number is used twice.
        /// </summary>
        internal static string NextManualNo(IEnumerable<string?> siblingNos, string? parentManualNo, bool topLevel)
        {
            var numbers = siblingNos.Select(n => (n ?? string.Empty).Trim()).Where(n => n.Length > 0).ToList();
            if (topLevel)
            {
                var maxMajor = numbers.Select(n => int.TryParse(n.Split('.')[0], out var v) ? v : 0).DefaultIfEmpty(0).Max();
                return $"{maxMajor + 1}.0";
            }

            string? prefix = null;
            var maxLast = 0;
            foreach (var n in numbers)
            {
                var dot = n.LastIndexOf('.');
                if (dot <= 0 || !int.TryParse(n[(dot + 1)..], out var last)) continue;
                if (prefix == null || last > maxLast)
                {
                    prefix = n[..dot];
                    maxLast = last;
                }
            }
            return prefix != null ? $"{prefix}.{maxLast + 1}" : $"{(parentManualNo ?? string.Empty).Trim()}.1";
        }

        public async Task<CreateManualResultDto> Handle(CreateManualCommand request, CancellationToken cancellationToken)
        {
            var dmManual = _mapper.Map<DmManual>(request.Manual);

            var manuals = _dbContext.DmManuals
                            .AsEnumerable()
                            .Select(i => new
                            {
                                Item = i,
                                ParsedSortOrder = int.TryParse(i.SortOrder, out int val) ? val : 0
                            })
                            .ToList();

            var parentId = request.Manual.CalledManualID;
            if (request.Manual.CalledMode.ToUpper() == "AddSameLevel".ToUpper())
            {
                parentId = manuals
                            .Where(x => x.Item.DM_ManualID == request.Manual.CalledManualID)
                            .Select(x => x.Item.ParentId)
                            .FirstOrDefault();
                if (parentId == 0) parentId = -1;
            }

            #region Sort Order / Addl Sort Order
            var maxSortOrder = 0;
            var parentAddlSortOrder = "";
            if (parentId == -1)
            {
                maxSortOrder = manuals.Max(x => x.ParsedSortOrder) + 1;
            }
            else
            {
                var sortOrder1 = manuals
                                .Where(x => x.Item.ParentId == parentId || x.Item.DM_ManualID == parentId)
                                .Max(x => (int?)x.ParsedSortOrder) ?? 0;
                maxSortOrder = sortOrder1 + 1;

                parentAddlSortOrder = manuals
                                        .Where(x => x.Item.DM_ManualID == parentId)
                                        .Select(x => x.Item.AddlSortOrder)
                                        .FirstOrDefault() ?? string.Empty;
            }

            var sortOrder = maxSortOrder.ToString().PadLeft(6, '0');
            var addlSortOrder = string.IsNullOrEmpty(parentAddlSortOrder)
                                ? sortOrder
                                : $"{parentAddlSortOrder} {sortOrder}";

            var subManualList = manuals
                                .Where(x => x.ParsedSortOrder >= maxSortOrder)
                                .OrderBy(x => x.Item.AddlSortOrder)
                                .ToList();

            foreach (var subManual in subManualList)
            {
                parentAddlSortOrder = subManualList
                                        .Where(x => x.Item.DM_ManualID == subManual.Item.ParentId)
                                        .Select(m => m.Item.AddlSortOrder)
                                        .FirstOrDefault();

                if (parentAddlSortOrder == null)
                {
                    parentAddlSortOrder = manuals
                                            .Where(x => x.Item.DM_ManualID == subManual.Item.ParentId)
                                            .Select(m => m.Item.AddlSortOrder)
                                            .FirstOrDefault();
                }
                if (parentAddlSortOrder != null) parentAddlSortOrder = parentAddlSortOrder + " ";
                subManual.Item.SortOrder = (subManual.ParsedSortOrder + 1).ToString().PadLeft(6, '0');
                subManual.Item.AddlSortOrder = $"{parentAddlSortOrder}{subManual.Item.SortOrder}";
            }
            #endregion

            #region Manual No
            // The highest number is found by comparing the numbers as numbers. It used to sort them as text, where
            // "1.9" comes after "1.10" and "9.0" after "10.0", so the 11th entry got the 10th entry's number again.
            var siblingNos = await _dbContext.DmManuals
                                 .Where(x => x.ParentId == parentId)
                                 .Select(m => m.ManualNo)
                                 .ToListAsync(cancellationToken);
            var parentManualNo = parentId == -1 ? null : await _dbContext.DmManuals
                                 .Where(x => x.DM_ManualID == parentId)
                                 .Select(m => m.ManualNo)
                                 .FirstOrDefaultAsync(cancellationToken);
            var nextManualNo = NextManualNo(siblingNos, parentManualNo, parentId == -1);
            #endregion

            #region Manual Level
            var manualLevel = 0;
            if (parentId != -1)
            {
                var parentManualLevel = await _dbContext.DmManuals
                                                   .Where(x => x.DM_ManualID == parentId)
                                                   .Select(m => m.ManualLevel)
                                                   .FirstOrDefaultAsync();
                manualLevel = parentManualLevel;
                if (request.Manual.CalledMode.ToUpper() == "Sub Level".ToUpper())
                {
                    manualLevel = parentManualLevel + 1;
                }
            }
            #endregion

            //Insert Manual
            dmManual.DM_ManualID = await _dbContext.NextIdAsync<DmManual>(ct: cancellationToken);
            dmManual.Origin = "HQ";
            dmManual.VesselID = -1;
            dmManual.ManualNo = nextManualNo;

            dmManual.ParentId = parentId;
            dmManual.ManualLevel = manualLevel;
            dmManual.SortOrder = sortOrder;
            dmManual.AddlSortOrder = addlSortOrder;
            dmManual.CreatedDate = DateTime.UtcNow;
            dmManual.ModifiedBy = null;
            dmManual.ModifiedDate = null;
            dmManual.IsDeleted = 0;

            long nextDM_StatusID = DMStatusConstant.STATUS_DRAFT; string notificationType = ManualNotificationType.ADD, notificationTitle = "New Manual Added";
            string nextApproval_HQRole = "", nextApproval_HQDept = "";
            if (request.Manual.IsToPublish)
            {
                nextDM_StatusID = DMStatusConstant.STATUS_RELEASE;
                notificationType = ManualNotificationType.PUBLISH;
                notificationTitle = "New Manual Published";
                if (request.Manual.IsApprovalRequired == 1)
                {
                    nextDM_StatusID = await _queries.GetNextApprovalStatusID(request.Manual.Approval_FlowID ?? -1, DMStatusConstant.STATUS_DRAFT);

                    if (nextDM_StatusID != DMStatusConstant.STATUS_RELEASE)
                    {
                        var nextApproval = await (
                                            from setup in _dbContext.DmApprovalAndMailSetups
                                            where setup.FlowId == request.Manual.Approval_FlowID
                                                && setup.DmStatusId == nextDM_StatusID
                                                && !_dbContext.DmManualApprovedLists.Any(a => a.DM_ManualID == dmManual.DM_ManualID
                                                                                            && a.UserRole == setup.UserRole && a.HQDept == setup.HQDept)
                                            orderby setup.ApprovalOrder
                                            select setup
                                         ).FirstOrDefaultAsync();
                        if (nextApproval != null)
                        {
                            nextApproval_HQRole = nextApproval.UserRole ?? "";
                            nextApproval_HQDept = nextApproval.HQDept ?? "";
                        }
                        notificationType = ManualNotificationType.APPROVE;
                        notificationTitle = "New Manual Published and Moved to next approval";
                    }
                }
            }

            dmManual.DmStatusId = nextDM_StatusID;
            dmManual.NextApproval_HQRole = nextApproval_HQRole;
            dmManual.NextApproval_HQDept = nextApproval_HQDept;

            _dbContext.DmManuals.Add(dmManual);

            // Insert Manual Version
            var dmManualVersion = _mapper.Map<DmManualVersion>(request.Manual);

            dmManualVersion.DM_ManualVersionID = await _dbContext.NextIdAsync<DmManualVersion>(ct: cancellationToken);
            dmManualVersion.DM_ManualID = dmManual.DM_ManualID;
            dmManualVersion.Origin = "HQ";
            dmManualVersion.VesselID = -1;
            dmManualVersion.Version = "1.0"; // Default version for new manuals
            dmManualVersion.IsActive = 1;
            dmManualVersion.IsDisabled = 0;

            _dbContext.DmManualVersions.Add(dmManualVersion);

            await _dbContext.SaveChangesAsync(cancellationToken);

            string remarks = "Manual Name: " + dmManual.ManualName;
            if (request.Manual.IsApprovalRequired == 1)
            {
                remarks = remarks + Environment.NewLine + "Approval Flow: " + request.Manual.Approval_FlowID;
            }
            else
            {
                remarks = remarks + Environment.NewLine + "Approval flow not required";
            }
            await _queries.AddToLog(dmManual.DM_ManualID, dmManualVersion.DM_ManualVersionID, "New Manual Added", remarks);

            await _queries.AddNotification(dmManual.DM_ManualID, dmManualVersion.DM_ManualVersionID, "", notificationType, notificationTitle, "Manual: " + request.Manual.ManualName);

            return new CreateManualResultDto
            {
                ManualId = dmManual.DM_ManualID,
                ManualVersionId = dmManualVersion.DM_ManualVersionID
            };
        }
    }
}
