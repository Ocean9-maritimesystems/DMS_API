using DMS.Data.EF.Constant;
using DMS.Data.EF.Context;
using DMS.Application.DTO.Manuals;
using DMS.Data.EF.Models.Manuals;
using DMS.Data.EF.Query.Manuals;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Handler.Manuals
{
    public class EditManualCommandHandler : IRequestHandler<EditManualCommand, CreateManualResultDto>
    {
        private readonly ManualGeneralQueries _queries;
        DmsContext _dbContext;

        public EditManualCommandHandler(DmsContext dbContext, ManualGeneralQueries queries)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext), "DmsContext cannot be null");
            _queries = queries;
        }

        public async Task<CreateManualResultDto> Handle(EditManualCommand request, CancellationToken cancellationToken)
        {
            var nl = Environment.NewLine;
            string remarks = "";
            var dmManual = await _dbContext.DmManuals
                            .Where(x => x.DM_ManualID == request.Manual.DM_ManualID && (x.IsDeleted ?? 0) == 0)
                            .FirstOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"Manual {request.Manual.DM_ManualID} does not exist.");

            var dmManualVersion = await _dbContext.DmManualVersions
                            .Where(x => x.DM_ManualVersionID == request.Manual.DM_ManualVersionID && x.DM_ManualID == dmManual.DM_ManualID)
                            .FirstOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"Version {request.Manual.DM_ManualVersionID} of manual {request.Manual.DM_ManualID} does not exist.");

            // The version the edit ends up in: this one, or a new one when a released manual's text changes.
            var currentVersion = dmManualVersion;

            // Each remark is built from the old value before it is overwritten.
            static string YesNo(int? value) => value == 0 ? "NO" : "YES";

            if (dmManual.DM_CategoryID != request.Manual.DM_CategoryID)
            {
                var fromCategory = await _dbContext.DmCategories
                                    .Where(c => c.DM_CategoryID == dmManual.DM_CategoryID)
                                    .Select(x => x.CategoryName)
                                    .FirstOrDefaultAsync(cancellationToken);

                var toCategory = await _dbContext.DmCategories
                                  .Where(c => c.DM_CategoryID == request.Manual.DM_CategoryID)
                                  .Select(x => x.CategoryName)
                                  .FirstOrDefaultAsync(cancellationToken);

                remarks += "Category changed from " + fromCategory + " to " + toCategory + nl;
                dmManual.DM_CategoryID = request.Manual.DM_CategoryID;
            }

            if (dmManual.ManualCode != request.Manual.ManualCode)
            {
                remarks += "Manual code changed from " + dmManual.ManualCode + " to " + request.Manual.ManualCode + nl;
                dmManual.ManualCode = request.Manual.ManualCode;
            }

            if (dmManual.ManualName != request.Manual.ManualName)
            {
                remarks += "Manual name changed from " + dmManual.ManualName + " to " + request.Manual.ManualName + nl;
                dmManual.ManualName = request.Manual.ManualName;
            }

            if (dmManual.IsMainTitle != request.Manual.IsMainTitle)
            {
                remarks += "Is Main Title changed from " + YesNo(dmManual.IsMainTitle) + " to " + YesNo(request.Manual.IsMainTitle) + nl;
                dmManual.IsMainTitle = request.Manual.IsMainTitle;
            }

            if (dmManual.IsAcknowledgementRequired != request.Manual.IsAcknowledgementRequired)
            {
                remarks += "Acknowledgement Required changed from " + YesNo(dmManual.IsAcknowledgementRequired) +
                           " to " + YesNo(request.Manual.IsAcknowledgementRequired) + nl;
                dmManual.IsAcknowledgementRequired = request.Manual.IsAcknowledgementRequired;
            }

            if (dmManual.CanExport != request.Manual.CanExport)
            {
                remarks += "Can Export changed from " + YesNo(dmManual.CanExport) + " to " + YesNo(request.Manual.CanExport) + nl;
                dmManual.CanExport = request.Manual.CanExport;
            }

            if (dmManual.IsApprovalRequired != request.Manual.IsApprovalRequired)
            {
                remarks += "Approval Required changed from " + YesNo(dmManual.IsApprovalRequired) +
                           " to " + YesNo(request.Manual.IsApprovalRequired) + nl;
                dmManual.IsApprovalRequired = request.Manual.IsApprovalRequired;
            }

            if (dmManual.Approval_FlowID != request.Manual.Approval_FlowID)
            {
                remarks += "Approval Flow changed from " + dmManual.Approval_FlowID + " to " + request.Manual.Approval_FlowID + nl;
                dmManual.Approval_FlowID = request.Manual.Approval_FlowID;
            }

            if (dmManualVersion.TextContents != request.Manual.TextContents)
            {
                if (dmManual.DmStatusId == DMStatusConstant.STATUS_RELEASE)
                {
                    // A released manual is never changed in place: the edit becomes a new draft version.
                    dmManual.DmStatusId = await _dbContext.DmStatuses
                                              .Where(x => x.StatusName == "Draft")
                                              .Select(x => (long?)x.DmStatusId)
                                              .FirstOrDefaultAsync(cancellationToken)
                                          ?? DMStatusConstant.STATUS_DRAFT;

                    var nextVersion = NextMajorVersion(dmManualVersion.Version);

                    currentVersion = new DmManualVersion
                    {
                        DM_ManualVersionID = await _dbContext.NextIdAsync<DmManualVersion>(ct: cancellationToken),
                        DM_ManualID = dmManual.DM_ManualID,
                        Origin = "HQ",
                        VesselID = -1,
                        Version = nextVersion,
                        IsActive = 1,
                        IsDisabled = 0,
                        TextContents = request.Manual.TextContents,
                        CreatedBy = request.Manual.UserID,
                        CreatedDate = DateTime.UtcNow,
                    };
                    _dbContext.DmManualVersions.Add(currentVersion);

                    dmManualVersion.IsActive = 0;
                    _dbContext.DmManualVersions.Update(dmManualVersion);

                    remarks += "New Manual version " + nextVersion + " added" + nl;
                }
                else
                {
                    dmManualVersion.TextContents = request.Manual.TextContents;

                    // The text itself is kept in the version; Compare Version shows the difference.
                    remarks += "Manual content changed" + nl;
                }
            }

            string notificationType = ManualNotificationType.EDIT, notificationTitle = "Manual details edited";
            if (request.Manual.IsToPublish)
            {
                long nextDM_StatusID = DMStatusConstant.STATUS_RELEASE;
                notificationType = ManualNotificationType.PUBLISH; notificationTitle = "Manual details edited and Released";

                string nextApproval_HQRole = "", nextApproval_HQDept = "";
                if (request.Manual.IsApprovalRequired == 1)
                {
                    nextDM_StatusID = await _queries.GetNextApprovalStatusID(request.Manual.Approval_FlowID ?? -1, DMStatusConstant.STATUS_DRAFT);

                    if (nextDM_StatusID != DMStatusConstant.STATUS_RELEASE)
                    {
                        var nextApproval = await _queries.GetNextApproverAsync(request.Manual.Approval_FlowID, nextDM_StatusID,
                                                                               dmManual.DM_ManualID, currentVersion.DM_ManualVersionID);
                        if (nextApproval != null)
                        {
                            nextApproval_HQRole = nextApproval.UserRole ?? "";
                            nextApproval_HQDept = nextApproval.HQDept ?? "";
                        }
                    }
                    notificationType = ManualNotificationType.APPROVE; notificationTitle = "Manual details edited and sent to next approval";
                }

                dmManual.DmStatusId = nextDM_StatusID;
                dmManual.NextApproval_HQRole = nextApproval_HQRole;
                dmManual.NextApproval_HQDept = nextApproval_HQDept;

                currentVersion.PublishedBy = request.Manual.UserID;
                currentVersion.PublishedDate = DateTime.UtcNow;
                if (nextDM_StatusID == DMStatusConstant.STATUS_RELEASE)
                {
                    currentVersion.ReleasedBy = request.Manual.UserID;
                    currentVersion.ReleasedDate = DateTime.UtcNow;
                }

                remarks += "Manual Published" + nl;
            }

            if (remarks.Trim().Length > 0)
            {
                dmManual.ModifiedBy = request.Manual.UserID;
                dmManual.ModifiedDate = DateTime.UtcNow;

                currentVersion.LastModifiedBy = request.Manual.UserID;
                currentVersion.LastModifiedDate = DateTime.UtcNow;

                _dbContext.DmManuals.Update(dmManual);
                if (currentVersion == dmManualVersion)
                    _dbContext.DmManualVersions.Update(dmManualVersion);

                // Add Log (AddToLog saves the pending changes too)
                await _queries.AddToLog(dmManual.DM_ManualID, currentVersion.DM_ManualVersionID, "Manual Edited",
                    "Manual Name: " + dmManual.ManualName + nl + remarks);

                await _queries.AddNotification(dmManual.DM_ManualID, currentVersion.DM_ManualVersionID, "", notificationType, notificationTitle,
                    "Manual: " + dmManual.ManualName);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            return new CreateManualResultDto
            {
                ManualId = dmManual.DM_ManualID,
                ManualVersionId = currentVersion.DM_ManualVersionID
            };
        }

        /// <summary>"2.3" -> "3.0". A version that is not "major.minor" (or empty) becomes "1.0" / its number + 1.</summary>
        private static string NextMajorVersion(string? version)
        {
            var majorText = (version ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return int.TryParse(majorText, out var major) ? $"{major + 1}.0" : "1.0";
        }
    }
}
