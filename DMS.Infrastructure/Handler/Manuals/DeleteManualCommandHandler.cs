using DMS.Data.EF.Constant;
using DMS.Data.EF.Context;
using DMS.Data.EF.Query.Manuals;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Handler.Manuals
{
    /// <summary>Marks a manual, its versions and its attachments deleted, and records who did it.</summary>
    public class DeleteManualCommandHandler : IRequestHandler<DeleteManualCommand, bool>
    {
        private readonly ManualGeneralQueries _queries;
        DmsContext _dbContext;

        public DeleteManualCommandHandler(DmsContext dbContext, ManualGeneralQueries queries)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext), "DmsContext cannot be null");
            _queries = queries;
        }

        public async Task<bool> Handle(DeleteManualCommand request, CancellationToken cancellationToken)
        {
            var dmManual = await _dbContext.DmManuals
                            .Where(x => x.DM_ManualID == request.Manual.DM_ManualID)
                            .FirstOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"Manual {request.Manual.DM_ManualID} does not exist.");

            if (dmManual.IsDeleted == 1)
                return true; // already deleted (a second click)

            dmManual.IsDeleted = 1;
            dmManual.ModifiedBy = request.Manual.UserID;
            dmManual.ModifiedDate = DateTime.UtcNow;
            _dbContext.DmManuals.Update(dmManual);

            var manualVersions = await _dbContext.DmManualVersions
                                    .Where(v => v.DM_ManualID == dmManual.DM_ManualID)
                                    .ToListAsync(cancellationToken);
            foreach (var version in manualVersions)
            {
                version.IsActive = 0;
            }

            var attachments = await _dbContext.DmManualAttachments
                                .Where(a => a.DM_ManualID == dmManual.DM_ManualID)
                                .ToListAsync(cancellationToken);
            foreach (var attachment in attachments)
            {
                attachment.IsDeleted = 1;
            }

            // AddToLog saves the changes above together with the log entry.
            await _queries.AddToLog(dmManual.DM_ManualID, -1, "Manual Deleted", "Manual Name: " + dmManual.ManualName);

            await _queries.AddNotification(dmManual.DM_ManualID, -1, "", ManualNotificationType.DELETE, "Manual deleted",
                "Manual: " + dmManual.ManualName);

            await _dbContext.SaveChangesAsync(cancellationToken);

            return true;
        }
    }
}
