using AutoMapper;
using DMS.Application.DTO.Circulars;
using DMS.Data.EF.Constant;
using DMS.Data.EF.Context;
using DMS.Data.EF.Features;
using DMS.Data.EF.Models.Circulars;
using DMS.Data.EF.Query.Circulars;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Handler
{
    public class CreateCircularCommandHandler : IRequestHandler<CreateCircularsCommand, long>
    {
        private readonly IMapper _mapper;
        private readonly CIRGeneralQueries _queries;
        private readonly CircularsContext _dbContext;
        private readonly AttachmentStorage _storage;

        public CreateCircularCommandHandler(CircularsContext dbContext, IMapper mapper, CIRGeneralQueries queries, AttachmentStorage storage)
        {
            _storage = storage;
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext), "CircularsContext cannot be null");
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper), "Mapper cannot be null");
            _queries = queries;
        }

        public async Task<long> Handle(CreateCircularsCommand request, CancellationToken cancellationToken)
        {
            try
            {
                var cir_Master = _mapper.Map<CIR_Master>(request.createCIR);

                // var cir_Number = await _queries.GetNextCIRNumbersByCategoryAsync(request.createCIR.CIR_CategoryID);
                // The form makes a temporary record when it opens (api/Circular/AddCirMaster, Status "Temp") and
                // sends its ID: the circular is saved into that record, under that ID. Only without one (or when it
                // is gone) is a new record added. Before, a new ID was given and the record "updated" under it - a
                // row that did not exist, so nothing was updated and EF reported a concurrency error.
                var tempId = request.createCIR.CIR_MasterID;
                var intoTemp = tempId > 0 && await _dbContext.CIR_Master.AsNoTracking()
                    .AnyAsync(m => m.CIR_MasterID == tempId, cancellationToken);
                cir_Master.CIR_MasterID = intoTemp ? tempId : await _dbContext.NextIdAsync<CIR_Master>(ct: cancellationToken);
                cir_Master.Origin = "HQ";
                cir_Master.VesselID = -1;
                // cir_Master.CIR_Number = cir_Number;

                cir_Master.CreatedDate = DateTime.UtcNow;
                cir_Master.ModifiedBy = null;
                cir_Master.ModifiedDate = null;
                cir_Master.IsDeleted = 0;

                if (intoTemp)
                    _dbContext.CIR_Master.Update(cir_Master);
                else
                    _dbContext.CIR_Master.Add(cir_Master);

                // Insert Blob
                var cir_Blob = _mapper.Map<CIR_Blob>(request.createCIR);

                cir_Blob.CIR_BlobID = await _dbContext.NextIdAsync<CIR_Blob>(ct: cancellationToken);
                cir_Blob.CIR_MasterID = cir_Master.CIR_MasterID;
                cir_Blob.Origin = "HQ";
                cir_Blob.VesselID = -1;
                cir_Blob.AddedOn = DateTime.UtcNow;

                // The attachment goes to the database, a local folder or the cloud, as BlobTableSettings says for CIR_Blob.
                var stored = await _storage.SaveAsync(AttachmentStorage.CircularTable, cir_Blob.CIR_BlobID,
                    cir_Blob.FileName, request.createCIR.BlobContents, cancellationToken);
                cir_Blob.BlobContents = stored.BlobContents;
                cir_Blob.Cloud_ContainerName = stored.Cloud_ContainerName;
                cir_Blob.Cloud_Directory = stored.Cloud_Directory;
                cir_Blob.HQ_Storage_Option = stored.HQ_Storage_Option;
                cir_Blob.HQ_Local_Folder = stored.HQ_Local_Folder;
                cir_Blob.HQ_DatabaseName = stored.HQ_DatabaseName;
                cir_Blob.VSL_Storage_Option = stored.VSL_Storage_Option;
                cir_Blob.VSL_DatabaseName = stored.VSL_DatabaseName;
                cir_Blob.VSL_Local_Folder = stored.VSL_Local_Folder;

                _dbContext.CIR_Blob.Add(cir_Blob);

                var allowedUsersToAck = await _dbContext.CIR_CategoryExt
                                           .Where(c => c.CIR_CategoryID == request.createCIR.CIR_CategoryID)
                                           .Select(x => x.AllowedUsersToAck)
                                           .FirstOrDefaultAsync(cancellationToken);

                var userRanksList = allowedUsersToAck?
                                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(u => u.Trim())
                                        .Where(u => !string.IsNullOrWhiteSpace(u))
                                        .ToList();

                if (userRanksList != null && userRanksList.Count > 0)
                {
                    var toAckEntities = new List<CIR_ToAck_Vsl>();

                    // Determine vessel list: use provided VesselIdList if present, otherwise fallback to -1
                    var vesselIds = (request.createCIR.VesselIdList != null && request.createCIR.VesselIdList.Any())
                                    ? request.createCIR.VesselIdList.Distinct().ToList()
                                    : new List<int> { -1 };

                    // One block of IDs for every vessel x rank row.
                    long idCounter = await _dbContext.NextIdAsync<CIR_ToAck_Vsl>(count: vesselIds.Count * userRanksList.Count, ct: cancellationToken);

                    // Insert a row for each vessel and each rank
                    foreach (var vesselId in vesselIds)
                    {
                        foreach (var rank in userRanksList)
                        {
                            toAckEntities.Add(new CIR_ToAck_Vsl
                            {
                                CIR_ToAck_VslID = idCounter++,
                                CIR_MasterID = cir_Master.CIR_MasterID,
                                Origin = "HQ",
                                VesselID = vesselId,
                                UserRank = rank,
                                IsActive = 1
                            });
                        }
                    }

                    if (toAckEntities.Count > 0)
                        _dbContext.CIR_ToAck_Vsl.AddRange(toAckEntities);
                }

                await _dbContext.SaveChangesAsync(cancellationToken);

                string notificationType = CIRNotificationType.ADD, notificationTitle = "New " + request.createCIR.CircularType + " Added";
                string remarks = request.createCIR.CircularType + ": " + cir_Master.CIR_Number;

                await _queries.AddToLog(cir_Master.CIR_MasterID, "New " + request.createCIR.CircularType + " Added", remarks);

                await _queries.AddNotification(cir_Master.CIR_MasterID, "", notificationType, notificationTitle, request.createCIR.CircularType + ": " + cir_Master.CIR_Number);

                return cir_Master.CIR_MasterID;
            }
            catch (Exception)
            {
                throw;
            }
        }
    }
}
