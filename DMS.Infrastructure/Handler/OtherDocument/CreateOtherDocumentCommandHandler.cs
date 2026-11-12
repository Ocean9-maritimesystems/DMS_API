using AutoMapper;
using DMS.Data.EF.Constant;
using DMS.Data.EF.Context;
using DMS.Data.EF.Features;
using DMS.Data.EF.Models.OtherDocument;
using DMS.Data.EF.Query.Circulars;
using MediatR;

namespace DMS.Data.EF.Handler
{
    public class CreateOtherDocumentCommandHandler : IRequestHandler<CreateOtherDocumentCommand, long>
    {
        private readonly IMapper _mapper;
        // The other-documents queries: the log goes to OtherDocument_Log and the notification to
        // OtherDocument_Notification (CIRGeneralQueries wrote them to CIR_Log / CIR_Notification).
        private readonly OtherDocumentGeneralQueries _queries;
        private readonly OtherDocumentsContext _dbContext;
        private readonly AttachmentStorage _storage;

        public CreateOtherDocumentCommandHandler(OtherDocumentsContext dbContext, IMapper mapper, OtherDocumentGeneralQueries queries, AttachmentStorage storage)
        {
            _storage = storage;
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext), "OtherDocumentsContext cannot be null");
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper), "Mapper cannot be null");
            _queries = queries;
        }

        public async Task<long> Handle(CreateOtherDocumentCommand request, CancellationToken cancellationToken)
        {
            var dto = request.createOtherDocument;
            var origin = string.IsNullOrWhiteSpace(dto.Origin) ? "HQ" : dto.Origin.Trim().ToUpperInvariant();
            // Added at the office: kept at the office (never sent to a vessel), filed under the selected vessel.
            var isForOfficeOnly = origin == "HQ" ? 1 : 0;
            var vesselId = isForOfficeOnly == 1 && dto.VesselID <= 0 ? SqlDb.OfficeOnly : dto.VesselID;
            var nextMasterId = await _dbContext.NextIdAsync<OtherDocumentMaster>(origin, vesselId, ct: cancellationToken);

            var otherDocument = new OtherDocumentMaster
            {
                OtherDocumentMasterID = nextMasterId,
                Origin = origin,
                VesselID = vesselId,
                IsForOfficeOnly = isForOfficeOnly,
                OtherDocument_TypeID = (int?)dto.OtherDocument_TypeID,
                DocumentNumber = dto.DocumentNumber ?? dto.DocumentTitle ?? "DOC-" + nextMasterId,
                DocumentTitle = dto.DocumentTitle ?? dto.Title ?? string.Empty,
                Status = dto.Status ?? "Active",
                Reference = dto.Reference,
                Comments = dto.Comments,
                UploadedOn = dto.UploadedOn ?? DateTime.UtcNow,
                UploadedBy = dto.UploadedBy,
                IsDeleted = 0,
                CreatedBy = dto.UploadedBy,
                CreatedDate = DateTime.UtcNow,
                ModifiedBy = null,
                ModifiedDate = null
            };

            _dbContext.OtherDocumentMaster.Add(otherDocument);

            var cir_Blob = new OtherDocument_Blob
            {
                OtherDocument_BlobID = await _dbContext.NextIdAsync<OtherDocument_Blob>(origin, vesselId, ct: cancellationToken),
                OtherDocumentMasterID = otherDocument.OtherDocumentMasterID,
                Origin = origin,
                VesselID = vesselId,
                IsForOfficeOnly = isForOfficeOnly,
                Description = dto.Description ?? dto.Comments ?? dto.DocumentTitle ?? dto.Title,
                FileName = dto.FileName,
                FileType = dto.FileType,
                OriginalFileType = dto.OriginalFileType,
                BlobSize = dto.BlobSize,
                AddedOn = DateTime.UtcNow
            };

            // The file goes to the database, a local folder or the cloud, as BlobTableSettings says for OtherDocument_Blob.
            if (dto.BlobContents is { Length: > 0 }) FileRules.Check(cir_Blob.FileName);
            var stored = await _storage.SaveAsync(AttachmentStorage.OtherDocumentTable, cir_Blob.OtherDocument_BlobID,
                cir_Blob.FileName, dto.BlobContents, cancellationToken);
            cir_Blob.BlobContents = stored.BlobContents;
            cir_Blob.Cloud_ContainerName = stored.Cloud_ContainerName;
            cir_Blob.Cloud_Directory = stored.Cloud_Directory;
            cir_Blob.HQ_Storage_Option = stored.HQ_Storage_Option;
            cir_Blob.HQ_Local_Folder = stored.HQ_Local_Folder;
            cir_Blob.HQ_DatabaseName = stored.HQ_DatabaseName;
            cir_Blob.VSL_Storage_Option = stored.VSL_Storage_Option;
            cir_Blob.VSL_DatabaseName = stored.VSL_DatabaseName;
            cir_Blob.VSL_Local_Folder = stored.VSL_Local_Folder;

            _dbContext.OtherDocument_Blob.Add(cir_Blob);

            // A failed save also removes the file just stored in a local folder or the cloud.
            await _storage.SaveOrDiscardAsync(() => _dbContext.SaveChangesAsync(cancellationToken));

            string notificationType = OtherDocumentNotificationType.ADD, notificationTitle = "New " + otherDocument.DocumentNumber + " Added";
            string remarks = otherDocument.DocumentNumber + ": " + otherDocument.Reference;

            await _queries.AddToLog(otherDocument.OtherDocumentMasterID, "New " + otherDocument.DocumentNumber + " Added", remarks);

            await _queries.AddNotification(otherDocument.OtherDocumentMasterID, "", notificationType, notificationTitle, otherDocument.DocumentNumber + ": " + otherDocument.Reference);

            return otherDocument.OtherDocumentMasterID;
        }
    }
}
