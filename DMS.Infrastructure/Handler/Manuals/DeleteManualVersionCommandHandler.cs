using DMS.Data.EF.Constant;
using DMS.Data.EF.Context;
using DMS.Data.EF.Query.Manuals;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Handler.Manuals
{
    /// <summary>
    /// Removes a manual's newest version when it has not been released yet (a draft made by editing a released
    /// manual). The version before it becomes the current version again and the manual goes back to its status.
    /// A released version is never removed: edit the manual to make a new version instead.
    /// </summary>
    public class DeleteManualVersionCommandHandler : IRequestHandler<DeleteManualVersionCommand, bool>
    {
        private readonly ManualGeneralQueries _queries;
        DmsContext _dbContext;

        public DeleteManualVersionCommandHandler(DmsContext dbContext, ManualGeneralQueries queries)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext), "DmsContext cannot be null");
            _queries = queries;
        }

        public async Task<bool> Handle(DeleteManualVersionCommand request, CancellationToken cancellationToken)
        {
            var dmManual = await _dbContext.DmManuals
                                .Where(x => x.DM_ManualID == request.Manual.DM_ManualID && (x.IsDeleted ?? 0) == 0)
                                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"Manual {request.Manual.DM_ManualID} does not exist.");

            // Versions not removed before, newest first (IDs come from Proc_NextID and only grow).
            var versions = await _dbContext.DmManualVersions
                                .Where(v => v.DM_ManualID == dmManual.DM_ManualID && (v.IsDisabled ?? 0) == 0)
                                .OrderByDescending(v => v.DM_ManualVersionID)
                                .Take(2)
                                .ToListAsync(cancellationToken);

            if (versions.Count == 0)
                throw new KeyNotFoundException($"Manual {dmManual.DM_ManualID} has no versions.");

            var latest = versions[0];
            if (request.Manual.DM_ManualVersionID > 0 && request.Manual.DM_ManualVersionID != latest.DM_ManualVersionID)
                throw new ArgumentException("Only the newest version of a manual can be deleted.");
            if (versions.Count == 1)
                throw new ArgumentException("This is the manual's only version. Delete the manual instead.");
            if (latest.ReleasedDate != null)
                throw new ArgumentException($"Version {latest.Version} has been released and cannot be deleted. Edit the manual to make a new version.");

            var previous = versions[1];

            latest.IsActive = 0;
            latest.IsDisabled = 1;
            latest.LastModifiedBy = request.Manual.UserID;
            latest.LastModifiedDate = DateTime.UtcNow;

            previous.IsActive = 1;

            // Back to where the manual was before the draft: released (or still a draft when nothing was released).
            dmManual.DmStatusId = previous.ReleasedDate != null ? DMStatusConstant.STATUS_RELEASE : DMStatusConstant.STATUS_DRAFT;
            dmManual.NextApproval_HQRole = "";
            dmManual.NextApproval_HQDept = "";
            dmManual.ModifiedBy = request.Manual.UserID;
            dmManual.ModifiedDate = DateTime.UtcNow;

            _dbContext.DmManualVersions.Update(latest);
            _dbContext.DmManualVersions.Update(previous);
            _dbContext.DmManuals.Update(dmManual);

            await _queries.VoidApprovalsAsync(dmManual.DM_ManualID, latest.DM_ManualVersionID);

            await _queries.AddToLog(dmManual.DM_ManualID, latest.DM_ManualVersionID, "Manual Version Deleted",
                "Manual Name: " + dmManual.ManualName + Environment.NewLine + "Version: " + latest.Version +
                Environment.NewLine + "Current version is now " + previous.Version);

            await _queries.AddNotification(dmManual.DM_ManualID, previous.DM_ManualVersionID, "", ManualNotificationType.DELETE,
                "Manual version " + latest.Version + " deleted", "Manual: " + dmManual.ManualName);

            await _dbContext.SaveChangesAsync(cancellationToken);

            return true;
        }
    }
}
