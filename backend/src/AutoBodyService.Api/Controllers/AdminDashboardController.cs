using AutoBodyService.Application.Appointments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/dashboard")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminDashboardController(IAdminOverviewQueries overview) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AdminDashboard>> Get(CancellationToken cancellationToken) =>
        Ok(await overview.GetDashboardAsync(cancellationToken));
}
