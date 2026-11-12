using DMS.Application.Interface;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DMS.API.Auth;

/// <summary>
/// Vessel sync calls are made by the ship servers, not by a logged-in user. The vessel is identified by
///   X-Vessel-Id header (sent by DMS.Vessel.Api), or else the vesselId in the URL or VesselID in the body.
/// The vessel must exist in V_Vessel. Its ID is put in HttpContext.Items for the actions to use.
///
/// There is no secret check: anyone who can reach this API can call it for any vessel ID. Keep the
/// sync endpoints reachable only from the vessels' network path (VPN, firewall rule or IP allow-list).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
[Obsolete("No secret check: any caller could act as any vessel. Use [VesselKey] (X-Vessel-Id + X-Vessel-Key).", error: true)]
public sealed class SyncVesselAttribute : TypeFilterAttribute
{
    public const string VesselIdHeader = "X-Vessel-Id";
    public const string ItemKey = "SyncVesselId";

    public SyncVesselAttribute() : base(typeof(SyncVesselFilter)) { }

    /// <summary>The calling vessel (set by the filter).</summary>
    public static int VesselIdOf(HttpContext context) => (int)context.Items[ItemKey]!;

    private sealed class SyncVesselFilter : IAsyncActionFilter
    {
        private readonly ISyncService _sync;

        public SyncVesselFilter(ISyncService sync) => _sync = sync;

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var http = context.HttpContext;

            if (!await _sync.IsInstalledAsync())
            {
                context.Result = new ObjectResult("Vessel sync is not set up in the office database (run Database/005_VesselSync.sql).")
                { StatusCode = StatusCodes.Status503ServiceUnavailable };
                return;
            }

            // Header first, then ?vesselId=, then VesselID in the body.
            int? vesselId = int.TryParse(http.Request.Headers[VesselIdHeader].ToString(), out var h) && h > 0 ? h : null;
            if (vesselId == null && context.ActionArguments.TryGetValue("vesselId", out var q) && q is int qi && qi > 0)
                vesselId = qi;
            if (vesselId == null)
            {
                foreach (var value in context.ActionArguments.Values)
                {
                    if (value == null || value.GetType().IsPrimitive) continue;
                    if (value.GetType().GetProperty("VesselID")?.GetValue(value) is int b && b > 0) { vesselId = b; break; }
                }
            }

            if (vesselId == null)
            {
                context.Result = new BadRequestObjectResult("Unknown or missing vessel ID.");
                return;
            }

            http.Items[ItemKey] = vesselId.Value;
            await next();
        }
    }
}
