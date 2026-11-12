using DMS.Data.EF.Constant;
using DMS.Data.EF.Context;
using DMS.Data.EF.Models.Manuals;
using DMS.Data.EF.Query.Manuals;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Handler.Manuals
{
    /// <summary>Approve (or send back) the manual's version at its current approval step.</summary>
    public class ApproveManualCommandHandler : IRequestHandler<ApproveManualCommand, bool>
    {
        private readonly ManualGeneralQueries _queries;
        DmsContext _dbContext;

        public ApproveManualCommandHandler(DmsContext dbContext, ManualGeneralQueries queries)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext), "DmsContext cannot be null");
            _queries = queries;
        }

        public async Task<bool> Handle(ApproveManualCommand request, CancellationToken cancellationToken)
        {
            var dmManual = await _dbContext.DmManuals
                                .Where(x => x.DM_ManualID == request.Manual.DM_ManualID && (x.IsDeleted ?? 0) == 0)
                                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"Manual {request.Manual.DM_ManualID} does not exist.");

            var dmManualVersion = await _dbContext.DmManualVersions
                                        .Where(x => x.DM_ManualVersionID == request.Manual.DM_ManualVersionID && x.DM_ManualID == dmManual.DM_ManualID)
                                        .FirstOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"Version {request.Manual.DM_ManualVersionID} of manual {request.Manual.DM_ManualID} does not exist.");

            // Only a manual inside its approval flow (between Draft and Release) can be approved or sent back.
            var currentStatus = dmManual.DmStatusId;
            if (currentStatus <= DMStatusConstant.STATUS_DRAFT || currentStatus >= DMStatusConstant.STATUS_RELEASE)
                throw new ArgumentException(request.isToSendBack
                    ? "This manual is not waiting for approval, so it cannot be sent back."
                    : "This manual is not waiting for approval.");

            var flowId = dmManual.Approval_FlowID ?? -1;
            var manualName = string.IsNullOrWhiteSpace(dmManual.ManualName) ? request.Manual.ManualName : dmManual.ManualName;

            // Everything below is saved together, or not at all.
            await using var transaction = _dbContext.Database.CurrentTransaction == null
                ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
                : null;

            if (!request.isToSendBack)
            {
                var user = await _queries.GetHQUser(request.Manual.UserID);

                var approvedListEntry = new DmManualApprovedList
                {
                    DmManualApprovedListId = await _dbContext.NextIdAsync<DmManualApprovedList>(ct: cancellationToken),
                    Origin = "HQ",
                    VesselID = -1,
                    DM_ManualID = dmManual.DM_ManualID,
                    UserId = request.Manual.UserID,
                    UserRole = user?.Role ?? "",
                    UserName = user?.UserName ?? "",
                    HQDept = user?.HQDept ?? "",
                    ApprovedDate = DateTime.UtcNow,
                    Comments = request.Manual.Comments
                };
                _dbContext.DmManualApprovedLists.Add(approvedListEntry);
                await _dbContext.SaveChangesAsync(cancellationToken);
                await _queries.TagApprovalAsync(approvedListEntry.DmManualApprovedListId, dmManualVersion.DM_ManualVersionID);
            }
            else
            {
                // Sent back: this version goes through the whole flow again when it is resubmitted.
                await _queries.VoidApprovalsAsync(dmManual.DM_ManualID, dmManualVersion.DM_ManualVersionID);
            }

            string nextApproval_HQRole = "", nextApproval_HQDept = "";
            long nextDM_StatusID;
            string notificationType = ManualNotificationType.APPROVE, notificationTitle = "Manual approved";

            if (request.isToSendBack)
            {
                nextDM_StatusID = await _queries.GetSendBackApprovalStatusID(flowId, currentStatus);
                // No send-back step set up for this status: back to the editor as a draft.
                if (nextDM_StatusID == currentStatus) nextDM_StatusID = DMStatusConstant.STATUS_DRAFT;
                notificationTitle = "Manual sent back for edit";
            }
            else
            {
                nextDM_StatusID = await _queries.GetNextApprovalStatusID(flowId, currentStatus);
                if (nextDM_StatusID == currentStatus)
                    throw new ArgumentException("The approval flow of this manual has no next step after its current status. " +
                                                "Check DM_ApprovalAndMailSetup for approval flow " + flowId + ".");
            }

            if (nextDM_StatusID == DMStatusConstant.STATUS_RELEASE)
            {
                notificationType = ManualNotificationType.PUBLISH;
                notificationTitle = "Manual approved and Released";
            }
            else if (nextDM_StatusID > DMStatusConstant.STATUS_DRAFT)
            {
                var nextApproval = await _queries.GetNextApproverAsync(dmManual.Approval_FlowID, nextDM_StatusID,
                                                                       dmManual.DM_ManualID, dmManualVersion.DM_ManualVersionID);
                if (nextApproval != null)
                {
                    nextApproval_HQRole = nextApproval.UserRole ?? "";
                    nextApproval_HQDept = nextApproval.HQDept ?? "";
                    notificationTitle = (request.isToSendBack ? "Manual sent back to " : "Manual approved and moved to next approval (")
                                        + nextApproval_HQRole + " - " + nextApproval_HQDept + (request.isToSendBack ? "" : ")");
                }
            }

            dmManual.DmStatusId = nextDM_StatusID;
            dmManual.NextApproval_HQRole = nextApproval_HQRole;
            dmManual.NextApproval_HQDept = nextApproval_HQDept;
            dmManual.ModifiedBy = request.Manual.UserID;
            dmManual.ModifiedDate = DateTime.UtcNow;

            dmManualVersion.LastModifiedBy = request.Manual.UserID;
            dmManualVersion.LastModifiedDate = DateTime.UtcNow;
            if (nextDM_StatusID == DMStatusConstant.STATUS_RELEASE)
            {
                dmManualVersion.ReleasedBy = request.Manual.UserID;
                dmManualVersion.ReleasedDate = DateTime.UtcNow;
            }

            _dbContext.DmManuals.Update(dmManual);
            _dbContext.DmManualVersions.Update(dmManualVersion);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _queries.AddToLog(dmManual.DM_ManualID, dmManualVersion.DM_ManualVersionID,
                request.isToSendBack ? "Manual Sent Back" : "Manual Approved",
                "Manual Name: " + manualName + Environment.NewLine + "Version: " + dmManualVersion.Version +
                (string.IsNullOrWhiteSpace(request.Manual.Comments) ? "" : Environment.NewLine + "Comments: " + request.Manual.Comments));

            await _queries.AddNotification(dmManual.DM_ManualID, dmManualVersion.DM_ManualVersionID, "", notificationType, notificationTitle, "Manual: " + manualName);

            if (transaction != null) await transaction.CommitAsync(cancellationToken);

            return true;
        }
    }
}
