using DMS.API.Auth;
using DMS.Application.DTO.Sync;
using DMS.Application.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DMS.API.Controllers;

/// <summary>
/// Called by the ship servers (DMS.Vessel.Api), never by the office web app.
/// No user login here: each call carries the vessel's sync key instead (see <see cref="VesselKeyAttribute"/>).
/// </summary>
[Route("api/[controller]")]
[ApiController]
[AllowAnonymous]
[VesselKey]
public class SyncController : ControllerBase
{
    private readonly ISyncService _service;
    private readonly ILogger<SyncController> _logger;

    public SyncController(ISyncService service, ILogger<SyncController> logger)
    {
        _service = service;
        _logger = logger;
    }

    private int VesselId => VesselKeyAttribute.VesselIdOf(HttpContext);

    /// <summary>
    /// GET api/Sync/Changes?max=500: the records changed at the office for this vessel (Sync_ChangeLog), each as it
    /// is now - same table, key and data (file contents are downloaded on board when first opened).
    /// </summary>
    [HttpGet("Changes")]
    public async Task<ActionResult<SyncChangesResponse>> GetChanges(int max = 500)
    {
        var result = await _service.GetChangesAsync(VesselId, max);
        if (result == null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Row-by-row sync is not set up in the office database (run Database/010_Sync_ChangeLog.sql).");
        _logger.LogInformation("Sync changes for vessel {VesselId}: {Count} record(s), more: {HasMore}",
            VesselId, result.Changes.Count, result.HasMore);
        return Ok(result);
    }

    /// <summary>POST api/Sync/ChangesConfirm: the ship has saved these changes (sets Sync_ChangeLog.DownloadedUtc).</summary>
    [HttpPost("ChangesConfirm")]
    public async Task<ActionResult<SyncChangesConfirmResult>> ConfirmChanges([FromBody] SyncChangesConfirmRequest request)
    {
        return Ok(await _service.ConfirmChangesAsync(VesselId, request.ChangeIds));
    }

    /// <summary>Largest upload from a vessel in one call (records with their files).</summary>
    private const long MaxUploadBytes = 300L * 1024 * 1024;

    /// <summary>
    /// POST api/Sync/Upload: everything made or changed on board (multipart/form-data: an "upload" part with
    /// SyncUploadRequest as JSON, then one "file" part per file, named by SyncUploadRow.FileRef). The form is read
    /// here, after the vessel key check, so a refused call never uploads its files.
    /// </summary>
    [HttpPost("Upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<ActionResult<SyncUploadResult>> Upload(CancellationToken ct)
    {
        if (!Request.HasFormContentType) return BadRequest("Expected multipart/form-data.");
        var form = await Request.ReadFormAsync(ct);

        SyncUploadRequest? request;
        try
        {
            request = System.Text.Json.JsonSerializer.Deserialize<SyncUploadRequest>(form["upload"].ToString(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException)
        {
            request = null;
        }
        if (request == null) return BadRequest("The \"upload\" part is missing or not valid JSON.");

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var f in form.Files.GetFiles("file"))
        {
            using var ms = new MemoryStream((int)f.Length);
            await f.CopyToAsync(ms, ct);
            files[f.FileName] = ms.ToArray();
        }

        var result = await _service.UploadAsync(VesselId, request, files);
        _logger.LogInformation("Sync upload from vessel {VesselId}: {Stored} of {Count} record(s) stored, {Files} file(s)",
            VesselId, result.Results.Count(r => r.Stored), result.Results.Count, files.Count);
        return Ok(result);
    }

    /// <summary>POST api/Sync/Heartbeat: link check plus what the ship is doing and still has to send.</summary>
    [HttpPost("Heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] SyncHeartbeatRequest request)
    {
        request.VesselID = VesselId;
        await _service.HeartbeatAsync(request);
        return NoContent();
    }

    /// <summary>GET api/Sync/FeatureFile/{table}/{id}: a suggestion file (SUG_Blob), downloaded when first opened on board.</summary>
    [HttpGet("FeatureFile/{table}/{id:long}")]
    public async Task<IActionResult> GetFeatureFile(string table, long id)
    {
        var file = await _service.GetFeatureFileAsync(VesselId, table, id);
        if (file == null) return NotFound();
        if (file.Value.Bytes.Length == 0) return NoContent(); // no file at the office: the ship stops asking
        return File(file.Value.Bytes, "application/octet-stream", file.Value.FileName);
    }

    /// <summary>GET api/Sync/ManualAttachment/{id}: one file, downloaded when first needed on board.</summary>
    [HttpGet("ManualAttachment/{id:long}")]
    public async Task<IActionResult> GetManualAttachment(long id)
    {
        var file = await _service.GetManualAttachmentBytesAsync(VesselId, id);
        if (file == null) return NotFound();
        if (file.Value.Bytes.Length == 0) return NoContent(); // no file at the office: the ship stops asking
        return File(file.Value.Bytes, MimeTypeOrDefault(file.Value.FileType), file.Value.FileName);
    }

    /// <summary>GET api/Sync/CircularAttachment/{id}</summary>
    [HttpGet("CircularAttachment/{id:long}")]
    public async Task<IActionResult> GetCircularAttachment(long id)
    {
        var file = await _service.GetCircularAttachmentBytesAsync(VesselId, id);
        if (file == null) return NotFound();
        if (file.Value.Bytes.Length == 0) return NoContent(); // no file at the office: the ship stops asking
        return File(file.Value.Bytes, MimeTypeOrDefault(file.Value.FileType), file.Value.FileName);
    }

    /// <summary>GET api/Sync/OtherDocumentFile/{id}: one other-document file, for this vessel's documents only.</summary>
    [HttpGet("OtherDocumentFile/{id:long}")]
    public async Task<IActionResult> GetOtherDocumentFile(long id)
    {
        var file = await _service.GetOtherDocumentFileBytesAsync(VesselId, id);
        if (file == null) return NotFound();
        if (file.Value.Bytes.Length == 0) return NoContent(); // no file at the office: the ship stops asking
        return File(file.Value.Bytes, MimeTypeOrDefault(file.Value.FileType), file.Value.FileName);
    }

    /// <summary>FileType holds either a MIME type or a file extension.</summary>
    private static string MimeTypeOrDefault(string? fileType)
    {
        if (string.IsNullOrWhiteSpace(fileType)) return "application/octet-stream";
        if (fileType.Contains('/')) return fileType;
        return fileType.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "pdf" => "application/pdf",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "doc" => "application/msword",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "xls" => "application/vnd.ms-excel",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "txt" => "text/plain",
            _ => "application/octet-stream",
        };
    }
}
