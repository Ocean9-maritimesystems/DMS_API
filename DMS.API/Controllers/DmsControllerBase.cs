using DMS.API.Auth;
using DMS.Application.DTO.Features;
using DMS.Data.EF.Context;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace DMS.API.Controllers;

/// <summary>
/// Every DMS controller requires a logged-in user. The user ID always comes from the token,
/// never from a userId sent in the request (those parameters are kept only for compatibility).
/// The logged-in user (<see cref="_me"/> / <see cref="Me"/>) is loaded once per request, before
/// any action runs, so every controller that inherits this class can use it directly.
/// </summary>
[Authorize]
[ApiController]
public abstract class DmsControllerBase : ControllerBase, IAsyncActionFilter
{
    /// <summary>Key used to share the loaded user with the rest of the request (services, middleware).</summary>
    public const string CurrentUserItemKey = "DMS.CurrentUser";

    protected string CurrentUserId =>
        User.FindFirst(DmsClaims.UserId)?.Value
        ?? throw new UnauthorizedAccessException("The token has no user ID.");

    protected string CurrentUserRole => User.FindFirst(DmsClaims.Role)?.Value ?? "";

    /// <summary>The logged-in office user. Filled in before every action; null only on [AllowAnonymous] actions.</summary>
    protected OfficeUser? _me;

    /// <summary>The logged-in office user, for actions that require one.</summary>
    protected OfficeUser Me => _me ?? throw new UnauthorizedAccessException("The logged-in user is not available.");

    /// <summary>Kept for existing callers; the user is already loaded, so this no longer queries the database.</summary>
    protected async Task<OfficeUser> MeAsync() => _me ??= await LoadMeAsync();

    protected bool IsAdmin => string.Equals(CurrentUserRole, "Admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs before every action of every derived controller and initialises <see cref="_me"/>.</summary>
    [NonAction]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var allowAnonymous = context.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any();

        if (User.Identity?.IsAuthenticated == true && User.FindFirst(DmsClaims.UserId) != null)
        {
            _me = await LoadMeAsync();
        }
        else if (!allowAnonymous)
        {
            context.Result = Unauthorized(new { message = "Your session has expired. Please log in again." });
            return;
        }

        await next();
    }

    private async Task<OfficeUser> LoadMeAsync()
    {
        if (HttpContext.Items.TryGetValue(CurrentUserItemKey, out var cached) && cached is OfficeUser loaded)
            return loaded;

        var id = CurrentUserId;
        var db = HttpContext.RequestServices.GetRequiredService<GeneralContext>();
        var row = await db.HqUsers.AsNoTracking()
            .Where(u => u.UserId == id)
            .Select(u => new { u.UserName, u.HQDept })
            .FirstOrDefaultAsync();
        var user = new OfficeUser(id, row?.UserName ?? User.FindFirst(DmsClaims.Name)?.Value ?? id, CurrentUserRole, row?.HQDept);

        HttpContext.Items[CurrentUserItemKey] = user;
        return user;
    }

    protected static async Task<List<UploadedFile>> ReadFilesAsync(IEnumerable<IFormFile>? files)
    {
        var list = new List<UploadedFile>();
        foreach (var f in files ?? Enumerable.Empty<IFormFile>())
        {
            if (f.Length == 0) continue;
            using var ms = new MemoryStream();
            await f.CopyToAsync(ms);
            list.Add(new UploadedFile(Path.GetFileName(f.FileName), f.ContentType, ms.ToArray()));
        }
        return list;
    }

    protected IActionResult FileOrNotFound(FileContent? file)
        => file == null ? NotFound() : File(file.Bytes, file.ContentType, file.FileName);

    protected IActionResult Done(string? error) => error == null ? Ok() : BadRequest(new { message = error });
}
