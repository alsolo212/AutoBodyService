using AutoBodyService.Application.Prices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/prices")]
public sealed class PricesController(IPriceCatalog prices) : ControllerBase
{
    /// <summary>Published indicative service prices in display order.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PublicPriceItem>>> List(CancellationToken cancellationToken) =>
        Ok(await prices.ListPublicAsync(cancellationToken));
}
