/* =====================================================================================================
   033_FileTable_Indexes.sql  -  run on the OFFICE (HQ) database (and, optionally, on each ship database).

   The file tables were only indexed by their own key, so "the files of this document / circular /
   suggestion" read the whole table - file contents included - every time. That made
   api/OtherDocument/Vessel/{id}/Documents slow (it counts the files of every document listed), and also
   opening a document's or a circular's files, and the sync's file steps.

   One index per file table on the parent's key. A nonclustered index holds that key and the table's own key
   (OtherDocument_BlobID ...), never the file contents, so counting or listing a parent's files reads a few
   small index pages. DM_Manual_Attachments already has one (IX_DM_Manual_Attachments_Manual).

   Safe to run again: an index that is already there is left alone. ONLINE is not used, so it also runs on
   SQL Server Express / Standard; on a large table it takes a moment and briefly blocks writes to it.
   ===================================================================================================== */

SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.OtherDocument_Blob', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.OtherDocument_Blob') AND name = N'IX_OtherDocument_Blob_Master')
BEGIN
    CREATE NONCLUSTERED INDEX IX_OtherDocument_Blob_Master ON dbo.OtherDocument_Blob (OtherDocumentMasterID);
    PRINT 'IX_OtherDocument_Blob_Master created.';
END;

IF OBJECT_ID(N'dbo.CIR_Blob', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CIR_Blob') AND name = N'IX_CIR_Blob_Master')
BEGIN
    CREATE NONCLUSTERED INDEX IX_CIR_Blob_Master ON dbo.CIR_Blob (CIR_MasterID);
    PRINT 'IX_CIR_Blob_Master created.';
END;

IF OBJECT_ID(N'dbo.SUG_Blob', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SUG_Blob') AND name = N'IX_SUG_Blob_Master')
BEGIN
    CREATE NONCLUSTERED INDEX IX_SUG_Blob_Master ON dbo.SUG_Blob (SUG_MasterID);
    PRINT 'IX_SUG_Blob_Master created.';
END;

-- The documents of a vessel (the list itself): VesselID is already the first column of IX_OtherDocumentMaster_OfficeOnly.

-- Check: the three file tables with their indexes.
SELECT OBJECT_NAME(i.object_id) AS TableName, i.name AS IndexName, i.type_desc
  FROM sys.indexes i
 WHERE i.object_id IN (OBJECT_ID(N'dbo.OtherDocument_Blob'), OBJECT_ID(N'dbo.CIR_Blob'), OBJECT_ID(N'dbo.SUG_Blob'))
   AND i.index_id > 0
 ORDER BY TableName, i.index_id;
GO
