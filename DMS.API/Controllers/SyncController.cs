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

    /// <summary>POST api/Sync/Push: acknowledgements made on board.</summary>
    [HttpPost("Push")]
    public async Task<ActionResult<SyncPushResult>> Push([FromBody] SyncPushRequest request)
    {
        request.VesselID = VesselId;
        var result = await _service.PushAsync(request);
        _logger.LogInformation(
            "Sync push from vessel {VesselId}: {Manual} manual and {Circular} circular acknowledgement(s) stored",
            VesselId, result.ManualAcksAccepted, result.CircularAcksAccepted);
        return Ok(result);
    }

    /// <summary>Largest upload of one document from a vessel (all its files together).</summary>
    private const long MaxOtherDocumentUploadBytes = 300L * 1024 * 1024;

    /// <summary>
    /// POST api/Sync/OtherDocument: one other document added on board, with its files (multipart/form-data:
    /// a "document" part with OtherDocumentPushDto as JSON, then the "files" parts in the same order).
    /// The form is read here, after the vessel key check, so a refused call never uploads its files.
    /// </summary>
    [HttpPost("OtherDocument")]
    [RequestSizeLimit(MaxOtherDocumentUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxOtherDocumentUploadBytes)]
    public async Task<ActionResult<OtherDocumentPushResult>> PushOtherDocument(CancellationToken ct)
    {
        if (!Request.HasFormContentType) return BadRequest("Expected multipart/form-data.");
        var form = await Request.ReadFormAsync(ct);

        OtherDocumentPushDto? doc;
        try
        {
            doc = System.Text.Json.JsonSerializer.Deserialize<OtherDocumentPushDto>(form["document"].ToString(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException)
        {
            doc = null;
        }
        if (doc == null) return BadRequest("The \"document\" part is missing or not valid JSON.");

        var files = new List<byte[]>();
        foreach (var f in form.Files.GetFiles("files"))
        {
            using var ms = new MemoryStream((int)f.Length);
            await f.CopyToAsync(ms, ct);
            files.Add(ms.ToArray());
        }

        var result = await _service.PushOtherDocumentAsync(VesselId, doc, files);
        _logger.LogInformation(
            "Sync push from vessel {VesselId}: other document {Number} ({Files} file(s)) {Outcome}",
            VesselId, doc.DocumentNumber, files.Count,
            result.Error ?? (result.AlreadyApplied ? "already stored" : $"stored as {result.Document?.OtherDocumentMasterID}"));
        return Ok(result);
    }

    /// <summary>POST api/Sync/PushRows: records made on board in the new feature tables, same keys (see SyncPushRowsRequest).</summary>
    [HttpPost("PushRows")]
    [RequestSizeLimit(MaxOtherDocumentUploadBytes)]
    public async Task<ActionResult<SyncPushRowsResult>> PushRows([FromBody] SyncPushRowsRequest request)
    {
        var result = await _service.PushRowsAsync(VesselId, request);
        _logger.LogInformation("Sync push rows from vessel {VesselId}: {Stored} of {Count} stored",
            VesselId, result.Results.Count(r => r.Stored), result.Results.Count);
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
