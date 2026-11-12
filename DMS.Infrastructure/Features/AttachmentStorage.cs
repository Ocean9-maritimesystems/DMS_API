using Azure.Storage;
using Azure.Storage.Blobs;
using DMS.Data.EF.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DMS.Data.EF.Features;

/// <summary>One row of dbo.BlobTableSettings: where the files of one blob table (TableName) are kept.</summary>
public sealed class BlobTableSetting
{
    public string? TableName { get; set; }
    public string? Cloud_AccountName { get; set; }
    public string? Cloud_AccountKey { get; set; }
    public string? Cloud_ContainerName { get; set; }
    public string? Cloud_Directory { get; set; }
    public string? HQ_Storage_Option { get; set; }
    public string? HQ_Local_Folder { get; set; }
    public string? HQ_DatabaseName { get; set; }
    public string? VSL_Storage_Option { get; set; }
    public string? VSL_DatabaseName { get; set; }
    public string? VSL_Local_Folder { get; set; }
}

/// <summary>
/// What gets written on the blob row (CIR_Blob / OtherDocument_Blob) for one saved file: the storage columns copied
/// from BlobTableSettings, and BlobContents only when the file is kept in the database.
/// </summary>
public sealed class StoredAttachment
{
    public byte[]? BlobContents { get; set; }
    public string? Cloud_ContainerName { get; set; }
    public string? Cloud_Directory { get; set; }
    public string? HQ_Storage_Option { get; set; }
    public string? HQ_Local_Folder { get; set; }
    public string? HQ_DatabaseName { get; set; }
    public string? VSL_Storage_Option { get; set; }
    public string? VSL_DatabaseName { get; set; }
    public string? VSL_Local_Folder { get; set; }
}

/// <summary>The columns of a blob row needed to find its file again.</summary>
public sealed record AttachmentRef(
    long BlobId,
    string? FileName,
    byte[]? BlobContents,
    string? HQ_Storage_Option,
    string? HQ_Local_Folder,
    string? Cloud_ContainerName,
    string? Cloud_Directory);

/// <summary>
/// Saves and reads attachment files according to dbo.BlobTableSettings (one row per blob table, keyed by TableName).
/// HQ_Storage_Option decides where the office copy goes:
///   Database - BlobContents on the blob row (the default when the table has no settings row);
///   Local    - a file under HQ_Local_Folder\{table}\{blobId}_{fileName};
///   Cloud    - an Azure blob {Cloud_Directory}/{table}/{blobId}_{fileName} in Cloud_ContainerName
///              (account Cloud_AccountName / Cloud_AccountKey).
/// The other settings columns (VSL_*, HQ_DatabaseName, ...) are copied onto the blob row as they are.
/// </summary>
public sealed class AttachmentStorage
{
    public const string Database = "Database";
    public const string Local = "Local";
    public const string Cloud = "Cloud";

    public const string CircularTable = "CIR_Blob";
    public const string OtherDocumentTable = "OtherDocument_Blob";

    private readonly GeneralContext _db;
    private readonly ILogger<AttachmentStorage> _logger;
    private readonly Dictionary<string, BlobTableSetting> _settings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Files this request wrote to a local folder or the cloud that were not there before, until the database save
    /// that refers to them succeeds (see SaveOrDiscardAsync). A file that replaced one of the same name is not listed:
    /// removing it would remove what the existing row points to.
    /// </summary>
    private readonly List<(string Table, AttachmentRef File)> _unsaved = new();

    public AttachmentStorage(GeneralContext db, ILogger<AttachmentStorage> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>The BlobTableSettings row of a blob table (Database storage when there is none).</summary>
    public async Task<BlobTableSetting> SettingsAsync(string table)
    {
        if (_settings.TryGetValue(table, out var cached)) return cached;
        var row = await _db.BlobTableSettings.AsNoTracking()
            .Where(b => b.TableName == table)
            .Select(b => new BlobTableSetting
            {
                TableName = b.TableName,
                Cloud_AccountName = b.Cloud_AccountName,
                Cloud_AccountKey = b.Cloud_AccountKey,
                Cloud_ContainerName = b.Cloud_ContainerName,
                Cloud_Directory = b.Cloud_Directory,
                HQ_Storage_Option = b.HQ_Storage_Option,
                HQ_Local_Folder = b.HQ_Local_Folder,
                HQ_DatabaseName = b.HQ_DatabaseName,
                VSL_Storage_Option = b.VSL_Storage_Option,
                VSL_DatabaseName = b.VSL_DatabaseName,
                VSL_Local_Folder = b.VSL_Local_Folder,
            })
            .FirstOrDefaultAsync();
        if (row == null)
        {
            _logger.LogWarning("No BlobTableSettings row for {Table}; attachments are kept in the database.", table);
            row = new BlobTableSetting { TableName = table, HQ_Storage_Option = Database };
        }
        _settings[table] = row;
        return row;
    }

    /// <summary>
    /// Saves one file of a blob table where the settings say, and returns the values for the blob row.
    /// With no file (null or empty) nothing is stored, but the settings columns are still returned.
    /// </summary>
    public async Task<StoredAttachment> SaveAsync(string table, long blobId, string? fileName, byte[]? bytes, CancellationToken ct = default)
    {
        var s = await SettingsAsync(table);
        var option = Normalize(s.HQ_Storage_Option);
        var result = new StoredAttachment
        {
            Cloud_ContainerName = Cut(s.Cloud_ContainerName, 150),
            Cloud_Directory = Cut(s.Cloud_Directory, 150),
            HQ_Storage_Option = option,
            HQ_Local_Folder = Cut(s.HQ_Local_Folder, 150) ?? "",
            HQ_DatabaseName = Cut(s.HQ_DatabaseName, 150) ?? "",
            VSL_Storage_Option = Cut(s.VSL_Storage_Option, 50) ?? Database,
            VSL_DatabaseName = Cut(s.VSL_DatabaseName, 150) ?? "",
            VSL_Local_Folder = Cut(s.VSL_Local_Folder, 150) ?? "",
        };
        if (bytes == null || bytes.Length == 0) return result;

        switch (option)
        {
            case Local:
            {
                var path = LocalPath(s.HQ_Local_Folder, table, blobId, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var isNew = !File.Exists(path);
                await File.WriteAllBytesAsync(path, bytes, ct);
                if (isNew) _unsaved.Add((table, new AttachmentRef(blobId, fileName, null, Local, s.HQ_Local_Folder, null, null)));
                break;
            }
            case Cloud:
            {
                var container = await ContainerAsync(s, s.Cloud_ContainerName, create: true, ct);
                var blob = container.GetBlobClient(CloudName(s.Cloud_Directory, table, blobId, fileName));
                var isNew = !(await blob.ExistsAsync(ct)).Value;
                await blob.UploadAsync(BinaryData.FromBytes(bytes), overwrite: true, ct);
                if (isNew) _unsaved.Add((table, new AttachmentRef(blobId, fileName, null, Cloud, null, s.Cloud_ContainerName, s.Cloud_Directory)));
                break;
            }
            default:
                result.BlobContents = bytes;
                break;
        }
        return result;
    }

    /// <summary>
    /// Runs the database save that refers to the files saved so far. If it fails, those files (new ones in a local folder
    /// or the cloud) are removed again, so a failed save leaves no file behind that no row points to; then the error goes on.
    /// </summary>
    public async Task<T> SaveOrDiscardAsync<T>(Func<Task<T>> save)
    {
        try
        {
            var result = await save();
            _unsaved.Clear();
            return result;
        }
        catch
        {
            foreach (var (table, file) in _unsaved)
                await DeleteAsync(table, file); // best effort, failures logged
            _unsaved.Clear();
            throw;
        }
    }

    /// <summary>The file of a blob row: BlobContents when it is in the database, else from the local folder or the cloud.</summary>
    public async Task<byte[]?> ReadAsync(string table, AttachmentRef row, CancellationToken ct = default)
    {
        if (row.BlobContents is { Length: > 0 }) return row.BlobContents;
        try
        {
            switch (Normalize(row.HQ_Storage_Option))
            {
                case Local:
                {
                    var path = LocalPath(row.HQ_Local_Folder, table, row.BlobId, row.FileName);
                    if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct);
                    _logger.LogWarning("Attachment {Table} {BlobId} not found at {Path}.", table, row.BlobId, path);
                    return null;
                }
                case Cloud:
                {
                    var s = await SettingsAsync(table);
                    var container = await ContainerAsync(s, row.Cloud_ContainerName, create: false, ct);
                    var blob = container.GetBlobClient(CloudName(row.Cloud_Directory, table, row.BlobId, row.FileName));
                    if (!(await blob.ExistsAsync(ct)).Value)
                    {
                        _logger.LogWarning("Attachment {Table} {BlobId} not found in cloud blob {Blob}.", table, row.BlobId, blob.Name);
                        return null;
                    }
                    return (await blob.DownloadContentAsync(ct)).Value.Content.ToArray();
                }
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not read attachment {Table} {BlobId}.", table, row.BlobId);
            return null;
        }
    }

    /// <summary>Removes the stored file of a blob row (local or cloud). Best effort: failures are only logged.</summary>
    public async Task DeleteAsync(string table, AttachmentRef row, CancellationToken ct = default)
    {
        try
        {
            switch (Normalize(row.HQ_Storage_Option))
            {
                case Local:
                {
                    var path = LocalPath(row.HQ_Local_Folder, table, row.BlobId, row.FileName);
                    if (File.Exists(path)) File.Delete(path);
                    break;
                }
                case Cloud:
                {
                    var s = await SettingsAsync(table);
                    var container = await ContainerAsync(s, row.Cloud_ContainerName, create: false, ct);
                    await container.GetBlobClient(CloudName(row.Cloud_Directory, table, row.BlobId, row.FileName))
                        .DeleteIfExistsAsync(cancellationToken: ct);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not delete attachment {Table} {BlobId}.", table, row.BlobId);
        }
    }

    /// <summary>"cloud" / "Azure" / "Blob" -> Cloud, "local" / "Folder" -> Local, anything else -> Database.</summary>
    public static string Normalize(string? option)
    {
        var o = (option ?? "").Trim();
        if (o.Equals(Cloud, StringComparison.OrdinalIgnoreCase) || o.Equals("Azure", StringComparison.OrdinalIgnoreCase)
            || o.Equals("Blob", StringComparison.OrdinalIgnoreCase)) return Cloud;
        if (o.Equals(Local, StringComparison.OrdinalIgnoreCase) || o.Equals("Folder", StringComparison.OrdinalIgnoreCase)) return Local;
        return Database;
    }

    private static string StoredName(long blobId, string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', '#', '?' })) name = name.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "file";
        if (name.Length > 120) name = name[^120..];
        return $"{blobId}_{name}";
    }

    private static string LocalPath(string? folder, string table, long blobId, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException($"BlobTableSettings: HQ_Local_Folder is empty for {table}.");
        return Path.Combine(folder.Trim(), table, StoredName(blobId, fileName));
    }

    private static string CloudName(string? directory, string table, long blobId, string? fileName)
    {
        var dir = (directory ?? "").Replace('\\', '/').Trim().Trim('/');
        var name = $"{table}/{StoredName(blobId, fileName)}";
        return dir.Length == 0 ? name : $"{dir}/{name}";
    }

    private static async Task<BlobContainerClient> ContainerAsync(BlobTableSetting s, string? containerName, bool create, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Cloud_AccountName) || string.IsNullOrWhiteSpace(s.Cloud_AccountKey))
            throw new InvalidOperationException($"BlobTableSettings: Cloud_AccountName / Cloud_AccountKey are empty for {s.TableName}.");
        if (string.IsNullOrWhiteSpace(containerName))
            throw new InvalidOperationException($"BlobTableSettings: Cloud_ContainerName is empty for {s.TableName}.");

        var account = s.Cloud_AccountName.Trim();
        var service = new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"),
            new StorageSharedKeyCredential(account, s.Cloud_AccountKey.Trim()));
        // Container names must be lower case in Azure.
        var container = service.GetBlobContainerClient(containerName.Trim().ToLowerInvariant());
        if (create) await container.CreateIfNotExistsAsync(cancellationToken: ct);
        return container;
    }

    private static string? Cut(string? value, int max)
    {
        if (value == null) return null;
        value = value.Trim();
        return value.Length <= max ? value : value[..max];
    }
}
