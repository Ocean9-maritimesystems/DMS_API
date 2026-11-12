using DMS.Application.Interface;
using DMS.Application.DTO;
using Microsoft.AspNetCore.Mvc;

namespace DMS.API.Controllers;

[Route("api/[controller]")]
public class VesselController : DmsControllerBase
{
    private readonly IVesselService _service;
    private readonly ISyncService _sync;
    private readonly ILogger<VesselController> _logger;
  
    public VesselController(IVesselService service, ISyncService sync, ILogger<VesselController> logger)
    {
        _service = service;
        _sync = sync;
        _logger = logger;
    }

    [HttpGet("GetVslType")]
    public async Task<ActionResult<string>> GetVslType()
    {
        _logger.LogInformation("Get Vsl Type List");

        var hqUsertList = await _service.GetVslTypeList();

        return Ok(hqUsertList);
    }

    [HttpGet("GetVessels")]
    public async Task<ActionResult<List<VesselListDto>>> GetVessels()
    {
        _logger.LogInformation("Get Vessel List");

        var hqUsertList = await _service.GetVesselList();

        return Ok(hqUsertList);
    }

    /// <summary>Everything sent to one vessel (manuals, circulars/alerts, other documents) and how far it is acknowledged.</summary>
    [HttpGet("GetVesselSyncItems")]
    public async Task<ActionResult<List<VesselSyncItemDto>>> GetVesselSyncItems(int vesselId)
    {
        _logger.LogInformation("Get sync items for vessel {VesselId}", vesselId);
        return Ok(await _service.GetVesselSyncItemsAsync(vesselId));
    }

    /// <summary>Sync runs for one vessel, newest first.</summary>
    [HttpGet("GetVesselSyncHistory")]
    public async Task<ActionResult<VesselSyncHistoryDto>> GetVesselSyncHistory(int vesselId)
    {
        _logger.LogInformation("Get sync history for vessel {VesselId}", vesselId);
        try
        {
            return Ok(await _service.GetVesselSyncHistoryAsync(vesselId));
        }
        catch (Exception ex)
        {
            // Said in the reply (ProblemDetails.detail), so the page shows why instead of "Something went wrong".
            _logger.LogError(ex, "Sync history for vessel {VesselId} failed", vesselId);
            return Problem(title: "The sync history could not be loaded.", detail: ex.GetBaseException().Message, statusCode: 500);
        }
    }

    /// <summary>Sync monitor: one row per vessel with link state, items waiting to go out and alerts not acknowledged.</summary>
    [HttpGet("GetSyncMonitor")]
    public async Task<ActionResult<VesselSyncMonitorDto>> GetSyncMonitor()
    {
        _logger.LogInformation("Get sync monitor");
        return Ok(await _service.GetSyncMonitorAsync());
    }

    /// <summary>
    /// Creates a new sync key for a vessel (replacing any earlier key). The key is returned once and not stored:
    /// put it in the vessel's appsettings.json as Sync:VesselKey.
    /// </summary>
    [HttpPost("GenerateSyncKey")]
    public async Task<ActionResult<VesselSyncKeyDto>> GenerateSyncKey(int vesselId)
    {
        if (!await _sync.IsInstalledAsync())
            return BadRequest("Vessel sync is not set up in the office database (run Database/005_VesselSync.sql).");

        var key = await _sync.GenerateVesselKeyAsync(vesselId, CurrentUserId);
        _logger.LogInformation("Sync key generated for vessel {VesselId} by {UserId}", vesselId, CurrentUserId);
        return Ok(new VesselSyncKeyDto { VesselID = vesselId, Key = key });
    }

    /// <summary>Sends everything to the vessel again on its next sync (the vessel also drops anything it should not have).</summary>
    [HttpPost("RequestResync")]
    public async Task<IActionResult> RequestResync(int vesselId)
    {
        if (!await _sync.IsInstalledAsync())
            return BadRequest("Vessel sync is not set up in the office database (run Database/005_VesselSync.sql).");

        await _sync.RequestResyncAsync(vesselId);
        _logger.LogInformation("Full resync requested for vessel {VesselId} by {UserId}", vesselId, CurrentUserId);
        return NoContent();
    }
}
