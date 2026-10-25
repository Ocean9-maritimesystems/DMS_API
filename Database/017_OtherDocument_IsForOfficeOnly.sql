/* =====================================================================================================
   017_OtherDocument_IsForOfficeOnly.sql  -  run on the OFFICE (HQ) database. Safe to run more than once.
   Run it BEFORE starting the API version that maps the new column.

   An Other Document added at the office now keeps the vessel it is about (VesselID = the selected vessel) and is
   marked office only with a flag, instead of VesselID -2:

     OtherDocumentMaster.IsForOfficeOnly   1 = added at the office, never sent to a vessel; 0 = normal
     OtherDocument_Blob.IsForOfficeOnly    same value as its document (the files of an office-only document)

   A document added at the office without a vessel keeps VesselID -2.

   What reads the flag:
     - Row sync (api/Sync/Changes): an office-only record is sent as a removal, so a ship never holds one.
     - Old sync (api/Sync/Pull) and file downloads from a ship: office-only documents are left out.
     - New ship databases (dbo.Vsl_CreateVesselDatabase, 011): office-only rows are not copied.
       Run 011_Vsl_CreateVesselDatabase.sql again after this script so the procedure has that filter.
     - Inbox: a new office-only document goes to the office inbox (-2), not to the vessel's crew.
   ===================================================================================================== */
SET NOCOUNT ON;
GO

-- 1. The columns ----------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.OtherDocumentMaster', 'IsForOfficeOnly') IS NULL
BEGIN
    ALTER TABLE dbo.OtherDocumentMaster ADD IsForOfficeOnly int NOT NULL
        CONSTRAINT DF_OtherDocumentMaster_IsForOfficeOnly DEFAULT (0);
    PRINT 'Added OtherDocumentMaster.IsForOfficeOnly.';
END;
GO

IF COL_LENGTH('dbo.OtherDocument_Blob', 'IsForOfficeOnly') IS NULL
BEGIN
    ALTER TABLE dbo.OtherDocument_Blob ADD IsForOfficeOnly int NOT NULL
        CONSTRAINT DF_OtherDocument_Blob_IsForOfficeOnly DEFAULT (0);
    PRINT 'Added OtherDocument_Blob.IsForOfficeOnly.';
END;
GO

-- 2. Existing office documents (VesselID -2) --------------------------------------------------------------
-- They become office only. Where 013 recorded the vessel a document is about (RelatedVesselID), that vessel
-- becomes its VesselID. The row-sync triggers log the change; the sync sends it as a removal (office only),
-- which a ship that never had the document simply ignores.
DECLARE @n int = 0;
IF COL_LENGTH('dbo.OtherDocumentMaster', 'RelatedVesselID') IS NOT NULL
    EXEC sp_executesql N'
UPDATE m
   SET m.IsForOfficeOnly = 1,
       m.VesselID = CASE WHEN m.RelatedVesselID > 0 AND EXISTS (SELECT 1 FROM dbo.V_Vessel v WHERE v.VesselID = m.RelatedVesselID)
                         THEN m.RelatedVesselID ELSE m.VesselID END
  FROM dbo.OtherDocumentMaster m
 WHERE m.VesselID = -2 AND m.IsForOfficeOnly = 0;
SET @n = @@ROWCOUNT;', N'@n int OUTPUT', @n = @n OUTPUT;
ELSE
BEGIN
    UPDATE dbo.OtherDocumentMaster SET IsForOfficeOnly = 1 WHERE VesselID = -2 AND IsForOfficeOnly = 0;
    SET @n = @@ROWCOUNT;
END;
PRINT CONCAT(@n, ' existing office document(s) marked office only.');
GO

-- Files follow their document (flag and vessel).
UPDATE b
   SET b.IsForOfficeOnly = 1,
       b.VesselID = m.VesselID
  FROM dbo.OtherDocument_Blob b
  JOIN dbo.OtherDocumentMaster m ON m.OtherDocumentMasterID = b.OtherDocumentMasterID
 WHERE m.IsForOfficeOnly = 1 AND (b.IsForOfficeOnly = 0 OR b.VesselID <> m.VesselID);
PRINT CONCAT(@@ROWCOUNT, ' file(s) of office documents marked office only.');
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.OtherDocumentMaster') AND name = 'IX_OtherDocumentMaster_OfficeOnly')
    CREATE INDEX IX_OtherDocumentMaster_OfficeOnly ON dbo.OtherDocumentMaster (VesselID, IsForOfficeOnly)
        INCLUDE (OtherDocument_TypeID, DocumentNumber, IsDeleted);
GO

-- 3. Inbox: an office-only document is news for the office, not for the vessel's crew ---------------------
IF OBJECT_ID('dbo.Inbox_Add', 'P') IS NOT NULL
EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_OtherDocumentMaster_Inbox ON dbo.OtherDocumentMaster AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @rows TABLE (ID bigint, VesselID int, ItemType varchar(20), Title nvarchar(500));
    INSERT @rows
    SELECT i.OtherDocumentMasterID,
           CASE WHEN i.IsForOfficeOnly = 1 THEN -2 ELSE i.VesselID END,
           ''Document'',
           LEFT(LTRIM(ISNULL(i.DocumentNumber, '''') + '' '' + ISNULL(i.DocumentTitle, '''')), 500)
      FROM inserted i
      LEFT JOIN dbo.OtherDocument_Type t ON t.OtherDocument_TypeID = i.OtherDocument_TypeID
     WHERE ISNULL(i.IsDeleted, 0) <> 1 AND ISNULL(i.DocumentTitle, '''') <> '''';
    DECLARE @id bigint, @vessel int, @type varchar(20), @title nvarchar(500);
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT ID, VesselID, ItemType, Title FROM @rows;
    OPEN c; FETCH NEXT FROM c INTO @id, @vessel, @type, @title;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC dbo.Inbox_Add @vessel, @type, @id, NULL, ''New'', @title, NULL;
        FETCH NEXT FROM c INTO @id, @vessel, @type, @title;
    END
    CLOSE c; DEALLOCATE c;
END');
GO

-- Certificates nearing expiry: the same rule (office-only certificates are reported to the office only).
IF OBJECT_ID('dbo.Inbox_Add', 'P') IS NOT NULL AND COL_LENGTH('dbo.OtherDocumentMaster', 'ReplacesID') IS NOT NULL
EXEC (N'
CREATE OR ALTER PROCEDURE dbo.Inbox_CertificateExpiry
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @rows TABLE (ID bigint, VesselID int, Action varchar(20), Title nvarchar(500));
    INSERT @rows
    SELECT m.OtherDocumentMasterID,
           CASE WHEN m.IsForOfficeOnly = 1 THEN -2 ELSE m.VesselID END,
           CASE WHEN m.ExpiryDate < CAST(GETDATE() AS date) THEN ''Expired'' ELSE ''Expiring'' END,
           LEFT(LTRIM(ISNULL(m.DocumentNumber, '''') + '' '' + ISNULL(m.DocumentTitle, '''')), 500)
      FROM dbo.OtherDocumentMaster m
      JOIN dbo.OtherDocument_Type t ON t.OtherDocument_TypeID = m.OtherDocument_TypeID AND t.IsCertificate = 1
     WHERE ISNULL(m.IsDeleted, 0) <> 1 AND m.ExpiryDate IS NOT NULL
       AND m.ExpiryDate < DATEADD(day, t.WarningDays, CAST(GETDATE() AS date))
       AND NOT EXISTS (SELECT 1 FROM dbo.OtherDocumentMaster n WHERE n.ReplacesID = m.OtherDocumentMasterID AND ISNULL(n.IsDeleted, 0) <> 1)
       AND NOT EXISTS (SELECT 1 FROM dbo.DMS_Inbox x WHERE x.ItemType = ''Certificate'' AND x.ItemID = m.OtherDocumentMasterID
                         AND x.Action = CASE WHEN m.ExpiryDate < CAST(GETDATE() AS date) THEN ''Expired'' ELSE ''Expiring'' END);
    DECLARE @id bigint, @vessel int, @action varchar(20), @title nvarchar(500);
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT ID, VesselID, Action, Title FROM @rows;
    OPEN c; FETCH NEXT FROM c INTO @id, @vessel, @action, @title;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC dbo.Inbox_Add @vessel, ''Certificate'', @id, NULL, @action, @title, NULL;
        FETCH NEXT FROM c INTO @id, @vessel, @action, @title;
    END
    CLOSE c; DEALLOCATE c;
END');
GO

-- 4. Inbox entries already sent to a vessel for documents that are now office only: hidden there.
IF OBJECT_ID('dbo.DMS_Inbox', 'U') IS NOT NULL
BEGIN
    UPDATE x SET x.VesselID = -2
      FROM dbo.DMS_Inbox x
      JOIN dbo.OtherDocumentMaster m ON m.OtherDocumentMasterID = x.ItemID
     WHERE x.ItemType IN ('Document', 'Certificate') AND m.IsForOfficeOnly = 1 AND x.VesselID <> -2;
    PRINT CONCAT(@@ROWCOUNT, ' inbox entr(ies) of office-only documents moved to the office inbox.');
END;
GO

PRINT '017_OtherDocument_IsForOfficeOnly: done. Now run 011_Vsl_CreateVesselDatabase.sql again.';
GO
