using DMS.Application;
using DMS.Application.DTO.OtherDocument;
using DMS.Application.DTO.Features;
using DMS.Application.Interface;
using DMS.API.Models.OtherDocument;
using DMS.Data.EF.Features;
using DMS.Data.EF.Models.OtherDocument;
using Microsoft.AspNetCore.Mvc;

namespace DMS.API.Controllers;

/// <summary>
/// Other Documents, and the folders they are filed in (OtherDocument_Folder). A folder holds Other Documents only.
/// </summary>
[Route("api/[controller]")]
public class OtherDocumentController : DmsControllerBase
{
    private readonly IOtherDocumentService _service;
    private readonly ILogger<OtherDocumentController> _logger;

    public OtherDocumentController(IOtherDocumentService service, ILogger<OtherDocumentController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet("GetTypes")]
    public async Task<ActionResult<List<OtherDocument_Type>>> GetTypes()
    {
        _logger.LogInformation("Get OtherDocument types");
        var types = await _service.GetDocumentTypesAsync();
        return Ok(types);
    }

    [HttpGet("GetDocuments")]
    public async Task<ActionResult<List<Vw_OtherDocument_MasterBlob>>> GetDocuments(int? vesselId = null, string? documentType = null)
    {
        _logger.LogInformation("Get OtherDocuments (vesselId={vesselId}, documentType={documentType})", vesselId, documentType);
        var list = await _service.GetDocumentsAsync(vesselId, documentType);
        return Ok(list);
    }

    // GET api/OtherDocument/nextnumber/{typeId}
    [HttpGet("nextnumber/{typeId:long}")]
    public async Task<IActionResult> GetNextNumber(long typeId)
    {
        var number = await _service.GetNextDocumentNumberAsync(typeId);
        return Ok(new { documentNumber = number });
    }

    [HttpGet("GetRecentOtherDocuments")]
    public async Task<ActionResult<List<Vw_OtherDocument_MasterBlob>>> GetRecentOtherDocuments(int limit = 10)
    {
        _logger.LogInformation("Get recent OtherDocuments (limit={limit})", limit);
        var list = await _service.GetRecentOtherDocumentsAsync(limit);
        return Ok(list);
    }

    [HttpGet("GetOtherDocumentDetails/{otherDocumentMasterId}")]
    public async Task<ActionResult<Vw_OtherDocument_MasterBlob>> GetOtherDocumentDetails(long otherDocumentMasterId)
    {
        _logger.LogInformation("Get OtherDocument details (id={otherDocumentMasterId})", otherDocumentMasterId);
        var details = await _service.GetOtherDocumentDetailsAsync(otherDocumentMasterId);
        if (details == null)
            return NotFound();
        return Ok(details);
    }

    [HttpPost("AddOtherDocument")]
    [RequestSizeLimit(150L * 1024 * 1024)] // the file travels inside the JSON as base64
    public async Task<ActionResult<long>> AddOtherDocument([FromBody] CreateOtherDocumentDto model)
    {
        if (model is null)
            return BadRequest("Payload is required.");

        model.UploadedBy = CurrentUserId; // from the token, never from the request

        var id = await _service.AddOtherDocumentAsync(model);
        return Created(string.Empty, id);
    }

    [HttpPost("AddOtherDocumentRecord")]
    public async Task<ActionResult<long>> AddOtherDocumentRecord()
    {
        var id = await _service.InsertOtherDocumentRecordAsync();
        return Ok(id);
    }

    [HttpDelete("RemoveTempOtherDocument")]
    public async Task<IActionResult> RemoveTempOtherDocument(long otherDocumentMasterId)
    {
        await _service.RemoveTempOtherDocumentAsync(otherDocumentMasterId);
        return Ok();
    }

    // ---------------------------------------------------------------- Folders

    /// <summary>GET api/OtherDocument/Folders - the folders this user may open.</summary>
    [HttpGet("Folders")]
    public async Task<IActionResult> FolderTree()
    {
        return Ok(await _service.GetFolderTreeAsync(Me));
    }

    /// <summary>POST api/OtherDocument/Folders - a new folder.</summary>
    [HttpPost("Folders")]
    public async Task<IActionResult> CreateFolder([FromBody] SaveFolder request)
    {
        var (id, error) = await _service.SaveFolderAsync(null, request, Me);
        return error == null ? Ok(new { id }) : BadRequest(new { message = error });
    }

    /// <summary>PUT api/OtherDocument/Folders/{id} - rename or move a folder.</summary>
    [HttpPut("Folders/{id:long}")]
    public async Task<IActionResult> UpdateFolder(long id, [FromBody] SaveFolder request)
    {
        var (_, error) = await _service.SaveFolderAsync(id, request, Me);
        return Done(error);
    }

    /// <summary>DELETE api/OtherDocument/Folders/{id} - an empty folder.</summary>
    [HttpDelete("Folders/{id:long}")]
    public async Task<IActionResult> DeleteFolder(long id)
    {
        return Done(await _service.DeleteFolderAsync(id, Me));
    }

    // ---------------------------------------------------------------- Documents in folders

    /// <summary>GET api/OtherDocument/Folders/{id}/Documents - the documents filed in this folder.</summary>
    [HttpGet("Folders/{id:long}/Documents")]
    public async Task<IActionResult> FolderDocuments(long id, int? vesselId)
    {
        return Ok(await _service.GetFolderDocumentsAsync(id, vesselId, Me));
    }

    /// <summary>GET api/OtherDocument/Folders/Unfiled/Documents?vesselId= - documents not in any folder.</summary>
    [HttpGet("Folders/Unfiled/Documents")]
    public async Task<IActionResult> UnfiledDocuments(int? vesselId)
    {
        return Ok(await _service.GetFolderDocumentsAsync(null, vesselId, Me));
    }

    /// <summary>GET api/OtherDocument/Vessel/{vesselId}/Documents - every document under a vessel, with its folder.</summary>
    [HttpGet("Vessel/{vesselId:int}/Documents")]
    public async Task<IActionResult> VesselDocuments(int vesselId)
    {
        return Ok(await _service.GetVesselDocumentsAsync(vesselId, Me));
    }

    /// <summary>POST api/OtherDocument/Folders/{id}/Documents (multipart) - a new document in this folder (office only, or sent to the vessel with sendToVessel=true).</summary>
    [HttpPost("Folders/{id:long}/Documents")]
    [RequestSizeLimit(200_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public Task<IActionResult> CreateFolderDocument(long id, [FromForm] CreateFolderDocumentRequest request)
    {
        return CreateDocumentIn(id, request);
    }

    /// <summary>POST api/OtherDocument/Folders/Unfiled/Documents (multipart) - a new document, not in a folder (office only, or sent to the vessel with sendToVessel=true).</summary>
    [HttpPost("Folders/Unfiled/Documents")]
    [RequestSizeLimit(200_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    public Task<IActionResult> CreateUnfiledDocument([FromForm] CreateFolderDocumentRequest request)
    {
        return CreateDocumentIn(null, request);
    }

    private async Task<IActionResult> CreateDocumentIn(long? folderId, CreateFolderDocumentRequest request)
    {
        var document = new NewFolderDocument(request.TypeId, request.DocumentNumber, request.Title, request.Status,
            request.Reference, request.Comments, request.VesselId, await ReadFilesAsync(request.Files), request.SendToVessel);
        var (newId, error) = await _service.CreateDocumentAsync(folderId, document, Me);
        return error == null ? Ok(new { id = newId }) : BadRequest(new { message = error });
    }

    /// <summary>PUT api/OtherDocument/Documents/{documentId}/Folder { folderId } - file a document (null = out of its folder).</summary>
    [HttpPut("Documents/{documentId:long}/Folder")]
    public async Task<IActionResult> MoveDocument(long documentId, [FromBody] MoveDocument request)
    {
        return Done(await _service.MoveDocumentAsync(documentId, request.FolderId, Me));
    }

    /// <summary>DELETE api/OtherDocument/Documents/{documentId} - a document added at the office (office only).</summary>
    [HttpDelete("Documents/{documentId:long}")]
    public async Task<IActionResult> DeleteDocument(long documentId)
    {
        return Done(await _service.DeleteDocumentAsync(documentId, Me));
    }

    /// <summary>GET api/OtherDocument/Documents/{documentId}/Files - the files attached to a document.</summary>
    [HttpGet("Documents/{documentId:long}/Files")]
    public async Task<IActionResult> DocumentFiles(long documentId)
    {
        return Ok(await _service.GetDocumentFilesAsync(documentId));
    }

    /// <summary>GET api/OtherDocument/Files/{blobId} - one file of a document (404 when it is not stored in the database).</summary>
    [HttpGet("Files/{blobId:long}")]
    public async Task<IActionResult> DocumentFile(long blobId)
    {
        return FileOrNotFound(await _service.GetDocumentFileAsync(blobId));
    }
}
