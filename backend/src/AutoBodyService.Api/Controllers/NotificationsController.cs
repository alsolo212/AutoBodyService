using System.Security.Claims;
using AutoBodyService.Application.Notifications;
using AutoBodyService.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "User")]
[Route("api/notifications")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class NotificationsController(INotificationHistory notifications) : ControllerBase
{
    /// <summary>Returns only the current account's notifications, optionally filtered by unread state.</summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] bool? unread, CancellationToken cancellationToken)
    {
        var recipient = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (recipient is null) return Unauthorized();
        return Ok(await notifications.GetAsync(recipient, unread, cancellationToken));
    }

    /// <summary>Marks an accessible notification as read; repeating the operation is harmless.</summary>
    [HttpPatch("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id, CancellationToken cancellationToken)
    {
        var recipient = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (recipient is null) return Unauthorized();
        return await notifications.MarkReadAsync(recipient, id, cancellationToken) ? NoContent() : NotFound();
    }

    /// <summary>Marks all notifications belonging to the current account as read.</summary>
    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken cancellationToken)
    {
        var recipient = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (recipient is null) return Unauthorized();
        await notifications.MarkAllReadAsync(recipient, cancellationToken);
        return NoContent();
    }
}
