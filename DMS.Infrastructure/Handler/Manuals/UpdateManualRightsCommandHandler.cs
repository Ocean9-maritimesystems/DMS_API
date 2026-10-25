using DMS.Data.EF.Context;
using DMS.Data.EF.Models.Manuals;
using DMS.Data.EF.Query.Manuals;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Handler.Manuals
{
    public class UpdateManualRightsCommandHandler : IRequestHandler<UpdateManualRightsCommand, bool>
    {
        private readonly ManualGeneralQueries _queries;
        private readonly DmsContext _dbContext;

        public UpdateManualRightsCommandHandler(DmsContext dbContext, ManualGeneralQueries queries)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext), "DmsContext cannot be null");
            _queries = queries;
        }

        public async Task<bool> Handle(UpdateManualRightsCommand request, CancellationToken cancellationToken)
        {
            var DM_ManualID = request.Manual.DM_ManualID;

            //Vessel and Roles to Ack
            var existingVslAcks = await _dbContext.DmManualToAckVsls
                                            .Where(x => x.DM_ManualID == DM_ManualID)
                                            .ToListAsync(cancellationToken);

            var vesselIdList = request.Manual.VesselIdList ?? new List<int>();
            var vesselRankList = request.Manual.VesselRankList ?? new List<string>();

            // Mark IsActive = 0 for items not in the new list
            foreach (var ack in existingVslAcks)
            {
                if (!vesselIdList.Contains(ack.VesselID) || !vesselRankList.Contains(ack.UserRank))
                {
                    ack.IsActive = 0;
                }
            }

            // Vessel / rank pairs that are new for this manual. Their IDs are reserved as one block from Proc_NextID:
            // asking once per row before saving would hand out the same ID again (the new rows are not in the table yet).
            var newVslPairs = new List<(int VesselID, string UserRank)>();
            foreach (int vesslID in vesselIdList.Distinct())
            {
                foreach (string vslRank in vesselRankList.Distinct())
                {
                    var existing = existingVslAcks.FirstOrDefault(x => x.VesselID == vesslID && x.UserRank == vslRank);
                    if (existing != null)
                    {
                        if (existing.IsActive == 0)
                        {
                            existing.IsActive = 1;
                        }
                    }
                    else
                    {
                        newVslPairs.Add((vesslID, vslRank));
                    }
                }
            }

            if (newVslPairs.Count > 0)
            {
                var nextVslAckId = await _dbContext.NextIdAsync<DmManualToAckVsl>(count: newVslPairs.Count, ct: cancellationToken);
                foreach (var (vesslID, vslRank) in newVslPairs)
                {
                    var roleToAck = new DmManualToAckVsl();
                    roleToAck.DmManualToAckVslId = nextVslAckId++;
                    roleToAck.Origin = "HQ";
                    roleToAck.DM_ManualID = DM_ManualID;
                    roleToAck.VesselID = vesslID;
                    roleToAck.UserRank = vslRank;
                    roleToAck.IsActive = 1;
                    _dbContext.DmManualToAckVsls.Add(roleToAck);
                }
            }

            //HQ user to Ack
            var existingHqAcks = await _dbContext.DmManualToAckHqs
                                           .Where(x => x.DM_ManualID == DM_ManualID)
                                           .ToListAsync(cancellationToken);

            var hqUsersIdList = request.Manual.HQ_UsersIDList ?? new List<long>();

            foreach (var ack in existingHqAcks)
            {
                if (!hqUsersIdList.Contains(ack.HqUsersId))
                {
                    ack.IsActive = 0;
                }
            }

            // New office users: one block of IDs, for the same reason as above.
            var newHqUsers = new List<long>();
            foreach (long hqUserID in hqUsersIdList.Distinct())
            {
                var existing = existingHqAcks.FirstOrDefault(x => x.HqUsersId == hqUserID);
                if (existing != null)
                {
                    if (existing.IsActive == 0)
                    {
                        existing.IsActive = 1;
                    }
                }
                else
                {
                    newHqUsers.Add(hqUserID);
                }
            }

            var nextHqAckId = newHqUsers.Count > 0
                ? await _dbContext.NextIdAsync<DmManualToAckHq>(count: newHqUsers.Count, ct: cancellationToken)
                : 0;
            foreach (long hqUserID in newHqUsers)
            {
                var hqToAck = new DmManualToAckHq();
                hqToAck.DmManualToAckHqid = nextHqAckId++;
                hqToAck.Origin = "HQ";
                hqToAck.VesselID = -1;
                hqToAck.HqUsersId = hqUserID;
                hqToAck.DM_ManualID = DM_ManualID;
                hqToAck.Role = "";
                hqToAck.IsActive = 1;
                _dbContext.DmManualToAckHqs.Add(hqToAck);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            await _queries.AddToLog(DM_ManualID, -1, "Manual Rights Assigned", "");

            return true;
        }
    }
}
