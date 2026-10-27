using DMS.Application.DTO.Features;

namespace DMS.Application.DTO.OtherDocument;

public class FolderItem
{
    public long OtherDocument_FolderID { get; set; }
    public int VesselID { get; set; }
    public string? VesselName { get; set; }
    public long? ParentID { get; set; }
    public string FolderName { get; set; } = "";
    /// <summary>The Other Documents filed in this folder.</summary>
    public int FileCount { get; set; }
    public bool CanEdit { get; set; }
}

    /// <summary>
    /// A folder to add or change. VesselId (new top-level folder only): a vessel = that vessel's folder; -1 = every vessel
    /// (administrators); none = office only. A sub-folder belongs to the same vessel as its parent.
    /// </summary>
    public sealed record SaveFolder(long? ParentId, string Name, int? VesselId);

    /// <summary>An Other Document as shown in a folder (OtherDocumentMaster.OtherDocument_FolderID).</summary>
    public class FolderDocument
    {
        public long OtherDocumentMasterID { get; set; }

        /// <summary>The vessel the document is about; -2 for an office document added without a vessel.</summary>
        public int VesselID { get; set; }
        public string? VesselName { get; set; }
        public long? OtherDocument_FolderID { get; set; }
        public string? FolderName { get; set; }
        /// <summary>OtherDocumentMaster.IsForOfficeOnly: added at the office and kept there, never sent to a vessel.</summary>
        public bool IsForOfficeOnly { get; set; }

        /// <summary>Same as IsForOfficeOnly (the name the web app reads).</summary>
        public bool IsOfficeOnly => IsForOfficeOnly;
        public long? OtherDocument_TypeID { get; set; }
        public string? DocumentType { get; set; }
        public string? DocumentNumber { get; set; }
        public string? DocumentTitle { get; set; }
        public string? Status { get; set; }
        public string? Reference { get; set; }
        public string? Comments { get; set; }
        public DateTime? UploadedOn { get; set; }
        public string? UploadedBy { get; set; }
        public string? Origin { get; set; }
        public int FileCount { get; set; }
        public long? FirstFileID { get; set; }
    }

    /// <summary>
    /// A new document added at the office. VesselId = the vessel it is about (the selected vessel); without one it is
    /// stored with VesselID -2. Normally it is marked IsForOfficeOnly = 1 and never sent to a vessel; with
    /// SendToVessel (a vessel is required) it is IsForOfficeOnly = 0 and goes to that vessel with the next sync.
    /// </summary>
    public sealed record NewFolderDocument(long TypeId, string DocumentNumber, string Title, string? Status, string? Reference,
        string? Comments, int? VesselId, List<UploadedFile> Files, bool SendToVessel = false);

    public sealed record MoveDocument(long? FolderId);