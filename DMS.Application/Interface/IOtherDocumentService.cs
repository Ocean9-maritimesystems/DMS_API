using DMS.Application.DTO.Features;
using DMS.Application.DTO.OtherDocument;

namespace DMS.Application.Interface;

public interface IOtherDocumentService
{
    Task<List<OtherDocument_Type>> GetDocumentTypesAsync();

    Task<List<Vw_OtherDocument_MasterBlob>> GetRecentOtherDocumentsAsync(int limit = 10);

    Task<List<Vw_OtherDocument_MasterBlob>> GetDocumentsAsync(int? vesselId = null, string? documentType = null);

    Task<Vw_OtherDocument_MasterBlob?> GetOtherDocumentDetailsAsync(long otherDocumentMasterId);

    Task<long> InsertOtherDocumentRecordAsync();

    Task RemoveTempOtherDocumentAsync(long otherDocumentMasterId);

    Task<long> AddOtherDocumentAsync(CreateOtherDocumentDto model);

    Task<string> GetNextDocumentNumberAsync(long typeId);

    // ---- Folders ----
    Task<List<FolderItem>> GetFolderTreeAsync(OfficeUser user);
    Task<(long? Id, string? Error)> SaveFolderAsync(long? folderId, SaveFolder request, OfficeUser user);
    Task<string?> DeleteFolderAsync(long folderId, OfficeUser user);

    // ---- Documents in folders ----
    Task<List<FolderDocument>> GetFolderDocumentsAsync(long? folderId, int? vesselId, OfficeUser user);
    Task<List<FolderDocument>> GetVesselDocumentsAsync(int vesselId, OfficeUser user);
    Task<(long? Id, string? Error)> CreateDocumentAsync(long? folderId, NewFolderDocument request, OfficeUser user);
    Task<string?> MoveDocumentAsync(long documentId, long? folderId, OfficeUser user);
    Task<string?> DeleteDocumentAsync(long documentId, OfficeUser user);
    Task<List<FileItem>> GetDocumentFilesAsync(long documentId);

    /// <summary>One file of an other document (OtherDocument_Blob), or null when it is not stored in the database.</summary>
    Task<FileContent?> GetDocumentFileAsync(long blobId);
}
