/*
    013_DocumentFolders.sql  -  Other Documents filed in folders (run after 012_NewFeatures.sql; safe to run again)

    Folders (FLD_Folder) and Other Documents are one feature: a document can sit in a folder, a folder shows its
    documents, and "New document" adds one straight into a folder.

      OtherDocumentMaster.FLD_FolderID     the folder the document is in; NULL = not in a folder.
      OtherDocumentMaster.RelatedVesselID  for a document added at the office (VesselID -2, office only, never sent to
                                           a vessel): the vessel it is about, so it shows under that vessel at the office.

    The column goes to the vessels with the existing row sync (OtherDocumentMaster is already synced). A document
    added on board in a folder brings its folder with it (api/Sync/OtherDocument); a document moved on board goes
    up as a "DocumentFolder" row (api/Sync/PushRows). Personal folders on board and their documents never leave the ship.
*/
SET NOCOUNT ON;
-- FLD_FolderID is renamed to OtherDocument_FolderID by 022_RenameFolderTable.sql.

IF OBJECT_ID('dbo.Feat_AddColumn', 'P') IS NULL
    THROW 50000, 'Run 012_NewFeatures.sql first.', 1;

IF COL_LENGTH('dbo.OtherDocumentMaster', 'OtherDocument_FolderID') IS NULL
    EXEC dbo.Feat_AddColumn 'OtherDocumentMaster', 'FLD_FolderID', 'bigint NULL';
EXEC dbo.Feat_AddColumn 'OtherDocumentMaster', 'RelatedVesselID', 'int NULL';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OtherDocumentMaster_Folder' AND object_id = OBJECT_ID('dbo.OtherDocumentMaster'))
   AND COL_LENGTH('dbo.OtherDocumentMaster', 'FLD_FolderID') IS NOT NULL
    EXEC sp_executesql N'CREATE INDEX IX_OtherDocumentMaster_Folder ON dbo.OtherDocumentMaster (FLD_FolderID) WHERE FLD_FolderID IS NOT NULL;';
GO

PRINT '013_DocumentFolders: OtherDocumentMaster.FLD_FolderID and RelatedVesselID are in place.';
