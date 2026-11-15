using AutoBodyService.Application.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "SuperAdmin")]
[Route("api/admin/audit-log")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminAuditController(IAdminAuditWriter audit) : ControllerBase
{
    /// <summary>Returns immutable administrative history. UTC lower bound is inclusive and upper bound exclusive.</summary>
    [HttpGet]
    public async Task<ActionResult<AuditPage>> Get([FromQuery] DateTimeOffset? fromUtc = null, [FromQuery] DateTimeOffset? toUtc = null,
        [FromQuery] string? action = null, [FromQuery] string? actorId = null, [FromQuery] string direction = "desc",
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        if ((fromUtc.HasValue && fromUtc.Value.Offset != TimeSpan.Zero) || (toUtc.HasValue && toUtc.Value.Offset != TimeSpan.Zero) ||
            fromUtc >= toUtc || action is { Length: > 100 } || actorId is { Length: > 450 } ||
            (Request.Query.ContainsKey("action") && string.IsNullOrWhiteSpace(action)) ||
            (Request.Query.ContainsKey("actorId") && string.IsNullOrWhiteSpace(actorId)) ||
            direction is not ("asc" or "desc") || page is < 1 or > 1_000_000 || pageSize is < 1 or > 100)
            return BadRequest(new ProblemDetails { Status = 400, Title = "Перевірте параметри журналу аудиту." });
        return Ok(await audit.ReadAsync(new(fromUtc, toUtc, action, actorId, direction == "desc", page, pageSize), cancellationToken));
    }
}
