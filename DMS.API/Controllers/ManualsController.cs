using DMS.Application.DTO.Manuals;
using DMS.Application.Interface;
using Microsoft.AspNetCore.Mvc;

namespace DMS.API.Controllers;

[Route("api/[controller]")]
public class ManualsController : DmsControllerBase
{
    private readonly IManualService _service;
    private readonly ILogger<ManualsController> _logger;

    public ManualsController(IManualService service, ILogger<ManualsController> logger)
    {
        _service = service;
        _logger = logger;
    }

    // Main Page - treeview
    [HttpGet("GetTreeViewManualList")]
    public async Task<ActionResult<List<Manual_TreeviewDto>>> GetTreeViewManualList(string? userId, string? calledMode = null)
    {
        _logger.LogInformation("Get Treeview Manual list");
        var manualList = await _service.GetTreeViewManualListAsync(CurrentUserId, calledMode);

        
        return Ok(manualList);
    }

    // Main Page - called mode
    [HttpGet("GetManualListBasedOnCalledMode")]
    public async Task<ActionResult<List<Manual_TreeviewDto>>> GetManualListBasedOnFilter(string calledMode, string? userId)
    {
        _logger.LogInformation("Get Manual list based on calledMode");
        var manualList = await _service.GetManualListBasedOnCalledModeAsync(calledMode, CurrentUserId);

        return Ok(manualList);
    }

    // Main Page - Right side manual content
    [HttpGet("GetManualContent")]
    public async Task<ActionResult<string?>> GetManualContent(long dm_ManualID, bool includeSubManuals)
    {
        _logger.LogInformation("Get Manual Content");
        var manualContent = await _service.GetManualContent(dm_ManualID, includeSubManuals);

        return Ok(manualContent);
    }

    // Used for edit and view manual
    [HttpGet("GetManualDetailsByVersionId/{DM_ManualVersionID}")]
    public async Task<ActionResult<ManualDetails>> GetEditManualDetailsByVersion(long DM_ManualVersionID)
    {
        var manualList = await _service.GetManualDetailsByVersionAsync(DM_ManualVersionID); 

        return Ok(manualList);
    }


    // Dashboard - Manual count
    [HttpGet("GetDashboardManualCounts")]
    public async Task<ActionResult<ManualCount_Dashboard>> GetDashboardManualCounts(string? userId)
    {
        _logger.LogInformation("Get Dashboard Manual count");
        var manualCount = await _service.GetDashboardManualCountsAsync(CurrentUserId);

        return Ok(manualCount);
    }

    // Dashboard/Main Page - User manual count
    [HttpGet("GetUserManualCounts")]
    public async Task<ActionResult<ManualCount_User>> GetUserManualCounts(string? userId)
    {
        _logger.LogInformation("Get User Manual count");
        var manualCount = await _service.GetUserManualCountAsync(CurrentUserId);

        return Ok(manualCount);
    }

    // User specific Manual List
    [HttpGet("GetUserManualListForCalledMode")]
    public async Task<ActionResult<List<ManualDetails>>> GetUserManualListForCalledMode(string? userID, string calledMode)
    {
        var manualList = await _service.GetUserManualListBasedOnCalledModeAsync(CurrentUserId, calledMode);

        return Ok(manualList);
    }

    // User Favouite Manual List
    [HttpGet("GetUserFavouiteManualList")]
    public async Task<ActionResult<List<ManualDetails>>> GetUserFavouiteManualList(string? userID)
    {
        _logger.LogInformation("Get User Favouite Manual list");

        var result = await _service.GetUserFavouiteManualListAsync(CurrentUserId);

        return Ok(result);
    }

    [HttpPost("RemoveFromFavourite")]
    public async Task<IActionResult> RemoveFromFavourite(long dm_ManualID)
    {
        await _service.RemoveFromFavouriteAsync(dm_ManualID, CurrentUserId);
        return Ok();
    }

    // Compare Version
    [HttpGet("GetManualVersionList")]
    public async Task<ActionResult<List<ManualVersionDetailsDto>>> GetManualVersionList(long dm_ManualID)
    {
        _logger.LogInformation("Get Compare Version List");
        var versionList = await _service.GetManualVersionDetailsAsync(dm_ManualID);

        return Ok(versionList);
    }

    // Version History dialog
    [HttpGet("GetManualVersionHistory")]
    public async Task<ActionResult<List<ManualVersionHistoryDto>>> GetManualVersionHistory(long dm_ManualID)
    {
        return Ok(await _service.GetManualVersionHistoryAsync(dm_ManualID));
    }

    // Compare Version - get manual basic details
    [HttpGet("GetManualSimpleDetails/{dm_ManualID}")]
    public async Task<ActionResult<Manual_SimpleDetails>> GetManualSimpleDetails(long dm_ManualID) 
    {
        var manualList = await _service.GetManualSimpleDetailsAsync(dm_ManualID); 

        return Ok(manualList);
    }

    // Compare Version - both manual contents
    [HttpGet("GetCompareVersion_Content")]
    public async Task<ActionResult<CompareVersionDetailsDto>> GetCompareVersion_Content(long dm_ManualID, long compare_ManualVersionID)
    {
        _logger.LogInformation("Get Compare Version Manual Content");
        var manualContent = await _service.GetCompareVersionDetailsAsync(dm_ManualID, compare_ManualVersionID);

        return Ok(manualContent);
    }

    // Get manual attachment details
    [HttpGet("GetManualAttachments")]
    public async Task<ActionResult<List<DmManualAttachment>>> GetManualAttachments(long dm_ManualID)
    {
        _logger.LogInformation("Get Manual Attachments");
        var manualAttachments = await _service.GetManualAttachmentListAsync(dm_ManualID);

        return Ok(manualAttachments);
    }

    // Add/Edit - Category list
    [HttpGet("GetManualCategory")]
    public async Task<ActionResult<ManualCategoryDto>> GetManualCategory()
    {
        _logger.LogInformation("Get Manual Category");

        var manualCategory = await _service.GetManualCategory();

        return Ok(manualCategory);
    }

    // Add/Edit - Approval Flow list
    [HttpGet("GetApprovalFlowList")]
    public async Task<ActionResult<ManualApprovalFlowListDto>> GetApprovalFlowList()
    {
        _logger.LogInformation("Get Approval Flow List");

        var flowList = await _service.GetApprovalFlowList();

        return Ok(flowList);
    }

    // Add/Edit - Approval Flow details
    [HttpGet("GetApprovalDetailsForFlowID/{FlowID}")]
    public async Task<ActionResult<ManualApprovalRoleListDto>> GetApprovalDetailsForFlowID(int flowID)
    {
        _logger.LogInformation("Get Approval Flow Details");

        var roleList = await _service.GetApprovalDetailsForFlowID(flowID);

        return Ok(roleList);
    }

    // Add/Edit - Manual Rights user list
    [HttpGet("GetManualRightsUsersAsync")]
    public async Task<ActionResult<AssignManualRightsDto>> GetManualRightsList()
    {
        _logger.LogInformation("Get Vessel and Rank List");

        var result = await _service.GetManualRightsUsersAsync();

        return Ok(result);
    }

    // Add/Edit - Selected Manual Rights user list
    [HttpGet("GetSelectedManualRightsList/{DM_ManualID}")]
    public async Task<ActionResult<AssignManualRightsDto>> GetSelectedManualRightsList(long dm_ManualID)
    {
        _logger.LogInformation("Get Manual assigned rights list");

        var result = await _service.GetDmManualAssignedRightsAsync(dm_ManualID);

        return Ok(result);
    }

    // Add/Edit - Selected Manual vessel ack list
    [HttpGet("GetManualUserAckList/{DM_ManualID}")]
    public async Task<ActionResult<List<ManualUserAckList>>> GetManualUserAckList(long dm_ManualID)
    {
        _logger.LogInformation("Get Manual user ack list");

        var result = await _service.GetManualUserAckListAsync(dm_ManualID);

        return Ok(result);
    }

    [HttpPost("AddManual")]
    public async Task<ActionResult<CreateManualResultDto>> AddManual(CreateManualDto manual)
    {
        manual.CreatedBy = CurrentUserId; // from the token, never from the request

        var DM_ManualID = await _service.AddDmManualAsync(manual);

        return Ok(DM_ManualID);
    }

    [HttpPost("EditManual")]
    public async Task<ActionResult> EditManual(EditManualDto manual)
    {
        SetActingUser(manual);
        await _service.UpdateDmManualAsync(manual);

        return Ok();
    }

    [HttpPost("ApproveManual")]
    public async Task<ActionResult> ApproveManual(ManualApproveDto request)
    {
        SetActingUser(request);

        var result = await _service.ApproveManualAsync(request, false);

        return Ok(result);
    }

    [HttpPost("PublishManual")]
    public async Task<ActionResult> PublishManual(ManualApproveDto request)
    {
        SetActingUser(request);

        var result = await _service.PublishManualAsync(request);

        return Ok(result);
    }

    [HttpPost("SendBackManual")]
    public async Task<ActionResult> SendBackManual(ManualApproveDto request)
    {
        SetActingUser(request);

        var DM_ManualVersionID = await _service.ApproveManualAsync(request, true);

        return Ok(DM_ManualVersionID);
    }

    [HttpDelete("DeleteManual/{DM_ManualID}")]
    public async Task<ActionResult> DeleteManual(long dm_ManualID)
    {
        // object
        // Validation

        await _service.DeleteDmManualAsync(dm_ManualID, CurrentUserId);

        return Ok();
    }

    [HttpDelete("DeleteManualVersion/{DM_ManualID}")]
    public async Task<ActionResult> DeleteManualVersion(long dm_ManualID)
    {
        // Validation

        await _service.DeleteDmManualVersionAsync(dm_ManualID, CurrentUserId);

        return Ok();
    }


    // Edit Manual rights details
    [HttpPost("UpdateManualRights")]
    public async Task<ActionResult> UpdateManualRights(ManualRightsDto manual)
    {
        // Validation

        await _service.UpdateManualRights(manual);

        return Ok();
    }

    [HttpPost("AddToFavourite")]
    public async Task<ActionResult> AddToFavourite(long dm_ManualID, string? userID)
    {
        await _service.AddToFavouriteAsync(dm_ManualID, CurrentUserId);
        return Ok();
    }

    // Get user notifications
    [HttpGet("GetManualNotifications")]
    public async Task<IActionResult> GetManualNotifications(int limit = 10)
    {
        var notifications = await _service.GetManualNotificationListAsync(limit, CurrentUserId);
        return Ok(notifications);
    }

    /// <summary>The user acting on a manual always comes from the login token; a UserID or role sent in the body is ignored.</summary>
    private void SetActingUser(ManualBaseDto dto)
    {
        dto.UserID = CurrentUserId;
        dto.UserRole = CurrentUserRole;
    }

    // Mark notification as read
    [HttpPost("MarkNotificationRead")]
    public async Task<IActionResult> MarkNotificationRead([FromQuery] long? notificationID,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] MarkNotificationReadRequest? body)
    {
        var id = notificationID ?? body?.NotificationId;
        if (id is null) return BadRequest("notificationId is required.");
        return await _service.MarkNotificationReadAsync(id.Value, CurrentUserId) ? NoContent() : NotFound();
    }
}

public sealed record MarkNotificationReadRequest(long NotificationId);
