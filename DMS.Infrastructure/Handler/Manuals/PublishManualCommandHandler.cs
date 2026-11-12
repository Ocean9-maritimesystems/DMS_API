using DMS.Data.EF.Constant;
using DMS.Data.EF.Context;
using DMS.Data.EF.Query.Manuals;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Handler.Manuals
{
    /// <summary>
    /// Publish = submit a draft without editing it (the same as Edit with "publish" ticked): released at once, or,
    /// when the manual needs approval, moved to the first approval step of its flow.
    /// </summary>
    public class PublishManualCommandHandler : IRequestHandler<PublishManualCommand, bool>
    {
        private readonly ManualGeneralQueries _queries;
        DmsContext _dbContext;

        public PublishManualCommandHandler(DmsContext dbContext, ManualGeneralQueries queries)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext), "DmsContext cannot be null");
            _queries = queries;
        }

        public async Task<bool> Handle(PublishManualCommand request, CancellationToken cancellationToken)
        {
            var dmManual = await _dbContext.DmManuals
                                   .Where(x => x.DM_ManualID == request.Manual.DM_ManualID && (x.IsDeleted ?? 0) == 0)
                                   .FirstOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"Manual {request.Manual.DM_ManualID} does not exist.");

            var dmManualVersion = await _dbContext.DmManualVersions
                                    .Where(x => x.DM_ManualVersionID == request.Manual.DM_ManualVersionID && x.DM_ManualID == dmManual.DM_ManualID)
                                    .FirstOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"Version {request.Manual.DM_ManualVersionID} of manual {request.Manual.DM_ManualID} does not exist.");

            if (dmManual.DmStatusId > DMStatusConstant.STATUS_DRAFT)
                throw new ArgumentException(dmManual.DmStatusId == DMStatusConstant.STATUS_RELEASE
                    ? "This manual is already released."
                    : "This manual is already waiting for approval.");

            string nextApproval_HQRole = "", nextApproval_HQDept = "";
            long nextDM_StatusID = DMStatusConstant.STATUS_RELEASE;
            string notificationType = ManualNotificationType.PUBLISH, notificationTitle = "Manual Released";

            if (dmManual.IsApprovalRequired == 1)
            {
                nextDM_StatusID = await _queries.GetNextApprovalStatusID(dmManual.Approval_FlowID ?? -1, DMStatusConstant.STATUS_DRAFT);
                if (nextDM_StatusID == DMStatusConstant.STATUS_DRAFT)
                    throw new ArgumentException("This manual needs approval, but its approval flow has no first step. " +
                                                "Choose an approval flow for the manual first.");

                if (nextDM_StatusID != DMStatusConstant.STATUS_RELEASE)
                {
                    var nextApproval = await _queries.GetNextApproverAsync(dmManual.Approval_FlowID, nextDM_StatusID,
                                                                           dmManual.DM_ManualID, dmManualVersion.DM_ManualVersionID);
                    if (nextApproval != null)
                    {
                        nextApproval_HQRole = nextApproval.UserRole ?? "";
                        nextApproval_HQDept = nextApproval.HQDept ?? "";
                    }
                    notificationType = ManualNotificationType.APPROVE;
                    notificationTitle = "Manual sent for approval" +
                        (nextApproval_HQRole.Length > 0 ? " (" + nextApproval_HQRole + " - " + nextApproval_HQDept + ")" : "");
                }
            }

            dmManual.DmStatusId = nextDM_StatusID;
            dmManual.NextApproval_HQRole = nextApproval_HQRole;
            dmManual.NextApproval_HQDept = nextApproval_HQDept;
            dmManual.ModifiedBy = request.Manual.UserID;
            dmManual.ModifiedDate = DateTime.UtcNow;

            dmManualVersion.IsActive = 1;
            dmManualVersion.IsDisabled = 0;
            dmManualVersion.PublishedBy = request.Manual.UserID;
            dmManualVersion.PublishedDate = DateTime.UtcNow;
            dmManualVersion.LastModifiedBy = request.Manual.UserID;
            dmManualVersion.LastModifiedDate = DateTime.UtcNow;
            if (nextDM_StatusID == DMStatusConstant.STATUS_RELEASE)
            {
                dmManualVersion.ReleasedBy = request.Manual.UserID;
                dmManualVersion.ReleasedDate = DateTime.UtcNow;
            }

            _dbContext.DmManuals.Update(dmManual);
            _dbContext.DmManualVersions.Update(dmManualVersion);

            await _queries.AddToLog(dmManual.DM_ManualID, dmManualVersion.DM_ManualVersionID, "Manual Published",
                "Manual Name: " + dmManual.ManualName + Environment.NewLine + "Version: " + dmManualVersion.Version);

            await _queries.AddNotification(dmManual.DM_ManualID, dmManualVersion.DM_ManualVersionID, "", notificationType, notificationTitle,
                "Manual: " + dmManual.ManualName);

            await _dbContext.SaveChangesAsync(cancellationToken);

            return true;
        }
    }
}
