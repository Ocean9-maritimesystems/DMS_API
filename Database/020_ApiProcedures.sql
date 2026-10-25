/* =====================================================================================================
   020_ApiProcedures.sql  -  run on the OFFICE (HQ) database after 005-019. Safe to run more than once.

   The API no longer builds SQL text: it reads and writes through Entity Framework, and calls these stored
   procedures where a query is dynamic or a large report:

     dbo.Api_TableExists        install checks (does a table exist yet)
     dbo.Api_Search             the search box (api/Search)
     dbo.Rpt_*                  the Reports page (api/Reports/{key}) and the Vessel sync history
     dbo.Api_SyncTableColumns   the synced tables and their columns (api/Sync/Changes)
     dbo.Sync_ReadRecords       the records of a synced table by key (api/Sync/Changes)
     dbo.Api_VesselIdRanges     V_Vessel.TableIDstart when the column exists (api/Sync/Pull)

   Run this script before starting the new API build.
   ===================================================================================================== */
SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.Sync_KeyColumn', 'FN') IS NULL OR TYPE_ID('dbo.Sync_RowList') IS NULL
    PRINT 'Note: run 010_Sync_ChangeLog.sql first, or dbo.Sync_ReadRecords and dbo.Api_SyncTableColumns cannot be created.';
GO

/* --------------------------------------------------------------------------------- Utilities ---- */

/* 1 when dbo.<@TableName> exists, else 0 (one column, Value). */
CREATE OR ALTER PROCEDURE dbo.Api_TableExists @TableName sysname
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CASE WHEN OBJECT_ID(N'dbo.' + QUOTENAME(@TableName), N'U') IS NULL THEN 0 ELSE 1 END AS [Value];
END
GO

/* ------------------------------------------------------------------------------------ Search ---- */

/* One search over manuals (number, name, text of the latest version), circulars and alerts, other documents
   and suggestions (api/Search). @Types: comma-separated Manual,Circular,Document,Suggestion.
   @W0..@W5: LIKE patterns with \ as the escape character, NULL when unused; every pattern given must match.
   @First: the first word, to centre the manual text snippet on it. Up to @PerType hits per type. */
CREATE OR ALTER PROCEDURE dbo.Api_Search
    @Types   varchar(100),
    @First   nvarchar(100),
    @W0      nvarchar(250),
    @W1      nvarchar(250) = NULL,
    @W2      nvarchar(250) = NULL,
    @W3      nvarchar(250) = NULL,
    @W4      nvarchar(250) = NULL,
    @W5      nvarchar(250) = NULL,
    @PerType int = 50
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @t varchar(102) = ',' + REPLACE(ISNULL(@Types, ''), ' ', '') + ',';

    DECLARE @hits TABLE (Grp int, Seq int, ItemType varchar(20), ItemID bigint, Number nvarchar(150), Title nvarchar(500),
                         Snippet nvarchar(max), VesselID int, [Date] datetime2);

    IF @t LIKE '%,Manual,%'
        INSERT @hits
        SELECT TOP (@PerType) 1, ROW_NUMBER() OVER (ORDER BY CASE WHEN m.ManualNo LIKE @W0 ESCAPE '\' OR m.ManualName LIKE @W0 ESCAPE '\' THEN 0 ELSE 1 END, m.ManualNo),
               'Manual', m.DM_ManualID, m.ManualNo, m.ManualName,
               SUBSTRING(v.TextContents, CASE WHEN CHARINDEX(@First, v.TextContents) > 150 THEN CHARINDEX(@First, v.TextContents) - 150 ELSE 1 END, 450),
               m.VesselID, COALESCE(v.ReleasedDate, v.CreatedDate)
          FROM dbo.DM_Manual m
         OUTER APPLY (SELECT TOP (1) x.TextContents, x.ReleasedDate, x.CreatedDate FROM dbo.DM_ManualVersion x
                       WHERE x.DM_ManualID = m.DM_ManualID ORDER BY x.DM_ManualVersionID DESC) v
         CROSS APPLY (SELECT m.ManualNo + ' ' + ISNULL(m.ManualName, '') + ' ' + ISNULL(CAST(v.TextContents AS nvarchar(max)), '') AS Hay) h
         WHERE ISNULL(m.IsDeleted, 0) <> 1
           AND h.Hay LIKE @W0 ESCAPE '\' AND (@W1 IS NULL OR h.Hay LIKE @W1 ESCAPE '\') AND (@W2 IS NULL OR h.Hay LIKE @W2 ESCAPE '\')
           AND (@W3 IS NULL OR h.Hay LIKE @W3 ESCAPE '\') AND (@W4 IS NULL OR h.Hay LIKE @W4 ESCAPE '\') AND (@W5 IS NULL OR h.Hay LIKE @W5 ESCAPE '\')
         ORDER BY CASE WHEN m.ManualNo LIKE @W0 ESCAPE '\' OR m.ManualName LIKE @W0 ESCAPE '\' THEN 0 ELSE 1 END, m.ManualNo;

    IF @t LIKE '%,Circular,%'
        INSERT @hits
        SELECT TOP (@PerType) 2, ROW_NUMBER() OVER (ORDER BY COALESCE(c.ReleasedDate, c.DateIssued, c.CreatedDate) DESC),
               CASE WHEN c.CircularType = 'Alert' THEN 'Alert' ELSE 'Circular' END, c.CIR_MasterID,
               c.CIR_Number, c.Title, LEFT(c.Remarks, 400), c.VesselID, COALESCE(c.ReleasedDate, c.DateIssued, c.CreatedDate)
          FROM dbo.CIR_Master c
         CROSS APPLY (SELECT ISNULL(c.CIR_Number, '') + ' ' + ISNULL(c.Title, '') + ' ' + ISNULL(c.Reference, '') + ' ' + ISNULL(c.Remarks, '') AS Hay) h
         WHERE ISNULL(c.IsDeleted, 0) <> 1
           AND h.Hay LIKE @W0 ESCAPE '\' AND (@W1 IS NULL OR h.Hay LIKE @W1 ESCAPE '\') AND (@W2 IS NULL OR h.Hay LIKE @W2 ESCAPE '\')
           AND (@W3 IS NULL OR h.Hay LIKE @W3 ESCAPE '\') AND (@W4 IS NULL OR h.Hay LIKE @W4 ESCAPE '\') AND (@W5 IS NULL OR h.Hay LIKE @W5 ESCAPE '\')
         ORDER BY COALESCE(c.ReleasedDate, c.DateIssued, c.CreatedDate) DESC;

    IF @t LIKE '%,Document,%'
        INSERT @hits
        SELECT TOP (@PerType) 3, ROW_NUMBER() OVER (ORDER BY COALESCE(d.UploadedOn, d.CreatedDate) DESC),
               'Document', d.OtherDocumentMasterID,
               d.DocumentNumber, d.DocumentTitle, LEFT(ISNULL(t.DocumentType + ': ', '') + ISNULL(d.Comments, ''), 400),
               d.VesselID, COALESCE(d.UploadedOn, d.CreatedDate)
          FROM dbo.OtherDocumentMaster d
          LEFT JOIN dbo.OtherDocument_Type t ON t.OtherDocument_TypeID = d.OtherDocument_TypeID
         CROSS APPLY (SELECT d.DocumentNumber + ' ' + d.DocumentTitle + ' ' + ISNULL(d.Reference, '') + ' ' + ISNULL(d.Comments, '') + ' '
                             + ISNULL(t.DocumentType, '') AS Hay) h
         WHERE ISNULL(d.IsDeleted, 0) <> 1
           AND h.Hay LIKE @W0 ESCAPE '\' AND (@W1 IS NULL OR h.Hay LIKE @W1 ESCAPE '\') AND (@W2 IS NULL OR h.Hay LIKE @W2 ESCAPE '\')
           AND (@W3 IS NULL OR h.Hay LIKE @W3 ESCAPE '\') AND (@W4 IS NULL OR h.Hay LIKE @W4 ESCAPE '\') AND (@W5 IS NULL OR h.Hay LIKE @W5 ESCAPE '\')
         ORDER BY COALESCE(d.UploadedOn, d.CreatedDate) DESC;

    IF @t LIKE '%,Suggestion,%'
        INSERT @hits
        SELECT TOP (@PerType) 4, ROW_NUMBER() OVER (ORDER BY s.SubmittedOn DESC),
               'Suggestion', s.SUG_MasterID, s.SuggestionNo, s.Title, LEFT(s.Status + ' - ' + ISNULL(s.Description, ''), 400),
               s.VesselID, s.SubmittedOn
          FROM dbo.SUG_Master s
         CROSS APPLY (SELECT ISNULL(s.SuggestionNo, '') + ' ' + s.Title + ' ' + ISNULL(s.Description, '') + ' ' + ISNULL(s.QuotedText, '') AS Hay) h
         WHERE s.IsDeleted = 0
           AND h.Hay LIKE @W0 ESCAPE '\' AND (@W1 IS NULL OR h.Hay LIKE @W1 ESCAPE '\') AND (@W2 IS NULL OR h.Hay LIKE @W2 ESCAPE '\')
           AND (@W3 IS NULL OR h.Hay LIKE @W3 ESCAPE '\') AND (@W4 IS NULL OR h.Hay LIKE @W4 ESCAPE '\') AND (@W5 IS NULL OR h.Hay LIKE @W5 ESCAPE '\')
         ORDER BY s.SubmittedOn DESC;

    SELECT ItemType, ItemID, Number, Title, Snippet, VesselID, CAST([Date] AS datetime2) AS [Date]
      FROM @hits
     ORDER BY Grp, Seq;
END
GO

/* ----------------------------------------------------------------------------------- Reports ---- */
/* api/Reports/{key}. Column names are camelCase: the Reports page shows and exports them as they are.
   @From / @To: rows from the start of @From up to (not including) @To. @VesselID -1 = the office rows (<= 0). */

/* ack-completed: acknowledgements received for manuals (DM_Manual_UserAck) and circulars / alerts (CIR_UserAck). */
CREATE OR ALTER PROCEDURE dbo.Rpt_AckCompleted
    @Max int = 20000, @Type varchar(20) = NULL, @VesselID int = NULL, @From datetime2 = NULL, @To datetime2 = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Max) x.* FROM (
        SELECT CASE WHEN a.VesselID > 0 THEN v.VslName ELSE 'Office' END AS vessel, 'Manual' AS type,
               m.ManualNo AS number, m.ManualName AS title, CAST(a.Version AS varchar(20)) AS version,
               a.UserName AS name, a.UserRank AS rank, a.DateRead AS acknowledgedOn, a.Comments AS comments
          FROM dbo.DM_Manual_UserAck a
          JOIN dbo.DM_Manual m ON m.DM_ManualID = a.DM_ManualID
          LEFT JOIN dbo.V_Vessel v ON v.VesselID = a.VesselID
         WHERE ISNULL(a.IsActive, 1) = 1
           AND (@Type IS NULL OR @Type = 'Manual')
           AND (@VesselID IS NULL OR a.VesselID = @VesselID OR (@VesselID = -1 AND a.VesselID <= 0))
           AND (@From IS NULL OR a.DateRead >= @From) AND (@To IS NULL OR a.DateRead < @To)
        UNION ALL
        SELECT CASE WHEN a.VesselID > 0 THEN v.VslName ELSE 'Office' END,
               CASE WHEN c.CircularType LIKE 'Alert%' THEN 'Alert' ELSE 'Circular' END,
               c.CIR_Number, c.Title, CAST(NULL AS varchar(20)),
               COALESCE(s.UserName, u.UserName), COALESCE(s.UserRank, u.Role), a.DateRead, a.Comments
          FROM dbo.CIR_UserAck a
          JOIN dbo.CIR_Master c ON c.CIR_MasterID = a.CIR_MasterID
          LEFT JOIN dbo.V_Vessel v ON v.VesselID = a.VesselID
          LEFT JOIN dbo.Vsl_Staff s ON a.VesselID > 0 AND s.Vsl_StaffID = a.Fleet_CrewID
          LEFT JOIN dbo.HQ_Users u ON a.VesselID <= 0 AND u.HQ_UsersID = a.Fleet_CrewID
         WHERE ISNULL(a.IsActive, 1) = 1
           AND (@Type IS NULL OR @Type = CASE WHEN c.CircularType LIKE 'Alert%' THEN 'Alert' ELSE 'Circular' END)
           AND (@VesselID IS NULL OR a.VesselID = @VesselID OR (@VesselID = -1 AND a.VesselID <= 0))
           AND (@From IS NULL OR a.DateRead >= @From) AND (@To IS NULL OR a.DateRead < @To)
    ) x
    ORDER BY x.acknowledgedOn DESC;
END
GO

/* circular-register: circulars and alerts with status, dates and acknowledgements received
   (the API adds acksPending / acksOverdue from dbo.Ack_GetPending). */
CREATE OR ALTER PROCEDURE dbo.Rpt_CircularRegister
    @Max int = 20000, @Type varchar(20) = NULL, @Status varchar(50) = NULL, @From datetime2 = NULL, @To datetime2 = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Max)
           c.CIR_MasterID AS id,
           CASE WHEN c.CircularType LIKE 'Alert%' THEN 'Alert' ELSE 'Circular' END AS type,
           c.CIR_Number AS number, cat.Category AS category, c.Title AS title, c.Reference AS reference,
           CASE c.Priority WHEN 1 THEN 'High' WHEN 2 THEN 'Medium' WHEN 3 THEN 'Low' END AS priority,
           c.Status AS status, c.DateIssued AS issued, c.ReleasedDate AS released, c.ValidUntil AS validUntil,
           sup.CIR_Number AS supersededBy,
           (SELECT COUNT(*) FROM dbo.CIR_UserAck a WHERE a.CIR_MasterID = c.CIR_MasterID AND ISNULL(a.IsActive, 1) = 1) AS acksReceived
      FROM dbo.CIR_Master c
      LEFT JOIN dbo.CIR_Category cat ON cat.CIR_CategoryID = c.CIR_CategoryID
      LEFT JOIN dbo.CIR_Master sup ON sup.CIR_MasterID = c.SupersededByID
     WHERE ISNULL(c.IsDeleted, 0) <> 1 AND ISNULL(c.Status, '') <> 'Temp'
       AND (@Type IS NULL OR @Type = CASE WHEN c.CircularType LIKE 'Alert%' THEN 'Alert' ELSE 'Circular' END)
       AND (@Status IS NULL OR c.Status = @Status)
       AND (@From IS NULL OR COALESCE(c.DateIssued, c.CreatedDate) >= @From)
       AND (@To IS NULL OR COALESCE(c.DateIssued, c.CreatedDate) < @To)
     ORDER BY COALESCE(c.DateIssued, c.CreatedDate) DESC;
END
GO

/* certificate-expiry was removed with the certificates (023_RemoveCertificates.sql). */
IF OBJECT_ID('dbo.Rpt_CertificateExpiry', 'P') IS NOT NULL DROP PROCEDURE dbo.Rpt_CertificateExpiry;
GO

/* document-register: other documents per vessel, where they came from (@Status = the source) and their files. */
CREATE OR ALTER PROCEDURE dbo.Rpt_DocumentRegister
    @Max int = 20000, @VesselID int = NULL, @Type nvarchar(150) = NULL, @Status varchar(30) = NULL,
    @From datetime2 = NULL, @To datetime2 = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Max) x.* FROM (
        SELECT CASE WHEN m.VesselID > 0 THEN v.VslName WHEN m.VesselID = -1 THEN 'All vessels' ELSE 'Office' END AS vessel,
               t.DocumentType AS type, m.DocumentNumber AS number, m.DocumentTitle AS title, m.Status AS status,
               CASE WHEN m.Origin = 'VSL' THEN 'From vessel' WHEN m.IsForOfficeOnly = 1 THEN 'Office only' ELSE 'Sent to vessel' END AS source,
               fo.FolderName AS folder,
               (SELECT COUNT(*) FROM dbo.OtherDocument_Blob b WHERE b.OtherDocumentMasterID = m.OtherDocumentMasterID) AS files,
               m.UploadedOn AS addedOn, m.UploadedBy AS addedBy
          FROM dbo.OtherDocumentMaster m
          LEFT JOIN dbo.OtherDocument_Type t ON t.OtherDocument_TypeID = m.OtherDocument_TypeID
          LEFT JOIN dbo.OtherDocument_Folder fo ON fo.OtherDocument_FolderID = m.OtherDocument_FolderID
          LEFT JOIN dbo.V_Vessel v ON v.VesselID = m.VesselID
         WHERE ISNULL(m.IsDeleted, 0) <> 1
           AND (@VesselID IS NULL OR m.VesselID = @VesselID)
           AND (@Type IS NULL OR t.DocumentType = @Type)
           AND (@From IS NULL OR m.UploadedOn >= @From) AND (@To IS NULL OR m.UploadedOn < @To)
    ) x
     WHERE (@Status IS NULL OR x.source = @Status)
     ORDER BY x.addedOn DESC;
END
GO

/* manual-register: manuals with category, status and current version. @TopLevelOnly 1 = main manuals only. */
CREATE OR ALTER PROCEDURE dbo.Rpt_ManualRegister
    @Max int = 20000, @Status varchar(50) = NULL, @TopLevelOnly bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    WITH cur AS (
        SELECT v.DM_ManualID, v.Version, v.PublishedDate, v.ReleasedDate,
               ROW_NUMBER() OVER (PARTITION BY v.DM_ManualID ORDER BY v.DM_ManualVersionID DESC) AS rn,
               COUNT(*) OVER (PARTITION BY v.DM_ManualID) AS versions
          FROM dbo.DM_ManualVersion v
         WHERE v.IsActive = 1)
    SELECT TOP (@Max)
           cat.CategoryName AS category, m.ManualNo AS number, m.ManualName AS title, m.ManualLevel AS level,
           st.StatusName AS status, cur.Version AS version, ISNULL(cur.versions, 0) AS versions,
           cur.PublishedDate AS published, cur.ReleasedDate AS released,
           CASE WHEN ISNULL(m.IsAcknowledgementRequired, 0) = 1 THEN 'Yes' ELSE 'No' END AS ackRequired,
           m.AckDueDays AS ackDueDays, COALESCE(m.ModifiedDate, m.CreatedDate) AS lastChanged
      FROM dbo.DM_Manual m
      LEFT JOIN dbo.DM_Category cat ON cat.DM_CategoryID = m.DM_CategoryID
      LEFT JOIN dbo.DM_Status st ON st.DM_StatusID = m.DM_StatusID
      LEFT JOIN cur ON cur.DM_ManualID = m.DM_ManualID AND cur.rn = 1
     WHERE ISNULL(m.IsDeleted, 0) <> 1
       AND (@Status IS NULL OR st.StatusName = @Status)
       AND (ISNULL(@TopLevelOnly, 0) = 0 OR NOT EXISTS (SELECT 1 FROM dbo.DM_Manual p WHERE p.DM_ManualID = m.ParentId))
     ORDER BY cat.CategoryName, m.SortOrder, m.ManualNo;
END
GO

/* vessel-sync: last contact / sync of every active vessel. */
CREATE OR ALTER PROCEDURE dbo.Rpt_VesselSync @VesselID int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF OBJECT_ID('dbo.Sync_Vessel', 'U') IS NULL
        THROW 50000, 'The vessel sync tables are not in this database (run Database/005_VesselSync.sql).', 1;
    SELECT v.VslName AS vessel, v.VslCode AS code, s.LastContactUtc AS lastContact, s.LastSuccessUtc AS lastSuccess,
           s.LastPullUtc AS lastPull, s.LastPushUtc AS lastPush,
           CASE WHEN s.VesselID IS NULL THEN 'Never synced' ELSE s.LastStatus END AS status,
           s.LastError AS lastError, s.PendingOnShip AS pendingOnShip, s.AppVersion AS appVersion,
           DATEDIFF(day, s.LastSuccessUtc, SYSUTCDATETIME()) AS daysSinceSync
      FROM dbo.V_Vessel v
      LEFT JOIN dbo.Sync_Vessel s ON s.VesselID = v.VesselID
     WHERE v.VesselID > 0 AND ISNULL(v.IsRetired, 0) = 0
       AND (@VesselID IS NULL OR v.VesselID = @VesselID)
     ORDER BY v.VslName;
END
GO

/* sync-log: the sync history (Vessel sync page, Sync history tab). */
CREATE OR ALTER PROCEDURE dbo.Rpt_SyncLog
    @Max int = 20000, @VesselID int = NULL, @Status nvarchar(20) = NULL, @From datetime2 = NULL, @To datetime2 = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF OBJECT_ID('dbo.Sync_Log', 'U') IS NULL
        THROW 50000, 'The vessel sync tables are not in this database (run Database/005_VesselSync.sql).', 1;
    SELECT TOP (@Max) v.VslName AS vessel, l.Direction AS direction, l.StartedUtc AS started, l.FinishedUtc AS finished,
           DATEDIFF(second, l.StartedUtc, l.FinishedUtc) AS seconds, l.ItemsCount AS items, l.Status AS status, l.Error AS error
      FROM dbo.Sync_Log l
      LEFT JOIN dbo.V_Vessel v ON v.VesselID = l.VesselID
     WHERE (@VesselID IS NULL OR l.VesselID = @VesselID)
       AND (@Status IS NULL OR l.Status = @Status)
       AND (@From IS NULL OR l.StartedUtc >= @From) AND (@To IS NULL OR l.StartedUtc < @To)
     ORDER BY l.StartedUtc DESC;
END
GO

/* suggestions: suggestions with status, vessel and what they are about. */
CREATE OR ALTER PROCEDURE dbo.Rpt_Suggestions
    @Max int = 20000, @VesselID int = NULL, @Status varchar(20) = NULL, @From datetime2 = NULL, @To datetime2 = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Max) s.SuggestionNo AS number, s.Title AS title,
           CASE WHEN s.VesselID > 0 THEN v.VslName WHEN s.VesselID = -1 THEN 'All vessels' ELSE 'Office' END AS vessel,
           COALESCE(s.SubmittedByName, s.SubmittedBy) AS submittedBy, s.SubmittedRank AS rank, s.SubmittedOn AS submittedOn,
           s.Priority AS priority, s.Status AS status, s.ItemType AS aboutType,
           CASE s.ItemType
                WHEN 'Manual' THEN (SELECT TOP (1) m.ManualNo + ' ' + ISNULL(m.ManualName, '') FROM dbo.DM_Manual m WHERE m.DM_ManualID = s.ItemID)
                WHEN 'Document' THEN (SELECT TOP (1) d.DocumentNumber + ' ' + d.DocumentTitle FROM dbo.OtherDocumentMaster d WHERE d.OtherDocumentMasterID = s.ItemID)
                WHEN 'Circular' THEN (SELECT TOP (1) ISNULL(c.CIR_Number, '') + ' ' + ISNULL(c.Title, '') FROM dbo.CIR_Master c WHERE c.CIR_MasterID = s.ItemID)
                WHEN 'Alert' THEN (SELECT TOP (1) ISNULL(c.CIR_Number, '') + ' ' + ISNULL(c.Title, '') FROM dbo.CIR_Master c WHERE c.CIR_MasterID = s.ItemID)
           END AS about,
           s.AssignedTo AS assignedTo, s.ModifiedOn AS lastUpdate,
           DATEDIFF(day, s.SubmittedOn, GETDATE()) AS daysOpen
      FROM dbo.SUG_Master s
      LEFT JOIN dbo.V_Vessel v ON v.VesselID = s.VesselID
     WHERE ISNULL(s.IsDeleted, 0) = 0
       AND (@VesselID IS NULL OR s.VesselID = @VesselID OR (@VesselID = -1 AND s.VesselID <= 0))
       AND (@Status IS NULL OR s.Status = @Status)
       AND (@From IS NULL OR s.SubmittedOn >= @From) AND (@To IS NULL OR s.SubmittedOn < @To)
     ORDER BY s.SubmittedOn DESC;
END
GO

/* ------------------------------------------------------------------------------- Vessel sync ---- */

/* api/Sync/Changes: one row per column of each active synced table (Sync_Table), with the table's settings,
   its single-column key (dbo.Sync_KeyColumn) and whether it has a VesselID column. */
CREATE OR ALTER PROCEDURE dbo.Api_SyncTableColumns
AS
BEGIN
    SET NOCOUNT ON;
    SELECT t.TableName,
           t.SortOrder,
           t.BlobColumn,
           t.AllVessels,
           CAST(dbo.Sync_KeyColumn(t.TableName) AS nvarchar(128)) AS KeyColumn,
           CAST(CASE WHEN COL_LENGTH('dbo.' + QUOTENAME(t.TableName), 'VesselID') IS NULL THEN 0 ELSE 1 END AS bit) AS HasVesselColumn,
           CAST(c.name AS nvarchar(128)) AS ColumnName
      FROM dbo.Sync_Table t
      JOIN sys.columns c ON c.object_id = OBJECT_ID('dbo.' + QUOTENAME(t.TableName), 'U')
     WHERE t.IsActive = 1
     ORDER BY t.SortOrder, c.column_id;
END
GO

/* api/Sync/Changes: the records of one synced table with these keys, every column except the file contents
   (Sync_Table.BlobColumn), exactly as stored. Only tables listed in Sync_Table can be read. */
CREATE OR ALTER PROCEDURE dbo.Sync_ReadRecords
    @TableName varchar(128),
    @Keys      dbo.Sync_RowList READONLY   -- KeyValue = the record keys (VesselID is not used)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @blob varchar(128), @key sysname, @columns nvarchar(max), @sql nvarchar(max);

    IF NOT EXISTS (SELECT 1 FROM dbo.Sync_Table WHERE TableName = @TableName AND IsActive = 1)
        THROW 50000, 'This table is not synced.', 1;
    SELECT @blob = BlobColumn FROM dbo.Sync_Table WHERE TableName = @TableName;
    SET @key = dbo.Sync_KeyColumn(@TableName);
    IF @key IS NULL
        THROW 50000, 'This table has no single-column key.', 1;

    SELECT @columns = STUFF((
        SELECT N', t.' + QUOTENAME(c.name)
          FROM sys.columns c
         WHERE c.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@TableName), N'U')
           AND (@blob IS NULL OR c.name <> @blob)
         ORDER BY c.column_id
           FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'');

    SET @sql = N'SELECT ' + @columns + N' FROM dbo.' + QUOTENAME(@TableName) + N' t'
             + N' WHERE t.' + QUOTENAME(@key) + N' IN (SELECT k.KeyValue FROM @Keys k);';
    EXEC sp_executesql @sql, N'@Keys dbo.Sync_RowList READONLY', @Keys = @Keys;
END
GO

/* api/Sync/Pull (fixed tables): the start of each vessel's ID range, V_Vessel.TableIDstart, or NULL when the column
   is not there. */
CREATE OR ALTER PROCEDURE dbo.Api_VesselIdRanges
AS
BEGIN
    SET NOCOUNT ON;
    IF COL_LENGTH('dbo.V_Vessel', 'TableIDstart') IS NULL
        SELECT VesselID, CAST(NULL AS bigint) AS TableIDstart FROM dbo.V_Vessel;
    ELSE
        EXEC sp_executesql N'SELECT VesselID, CAST(TableIDstart AS bigint) AS TableIDstart FROM dbo.V_Vessel;';
END
GO

PRINT '020_ApiProcedures: the API procedures are in place.';
