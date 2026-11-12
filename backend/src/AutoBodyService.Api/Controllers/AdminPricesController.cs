using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoBodyService.Application.Prices;
using AutoBodyService.Domain.Prices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/prices")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminPricesController(IPriceCatalog prices) : ControllerBase
{
    /// <summary>All catalogue entries, including hidden and archived records.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminPriceItem>>> List(CancellationToken cancellationToken) =>
        Ok(await prices.ListAdminAsync(cancellationToken));

    /// <summary>Read a single catalogue entry.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AdminPriceItem>> Get(Guid id, CancellationToken cancellationToken)
    {
        var item = await prices.GetAsync(id, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    /// <summary>Create an indicative price in UAH.</summary>
    [HttpPost]
    public async Task<ActionResult<AdminPriceItem>> Create(CreatePriceRequest request, CancellationToken cancellationToken)
    {
        if (!PriceListItem.ValidName(request.Name) || !PriceListItem.ValidDescription(request.Description) || request.Price < 0) return Invalid();
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actorId is null) return Unauthorized();
        var item = await prices.CreateAsync(request.Name!, request.Description, request.Price!.Value, request.IsFrom!.Value,
            request.SortOrder!.Value, request.IsPublished!.Value, actorId, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = item.Id }, item);
    }

    /// <summary>Change selected values, visibility, ordering, or archived state using the current version.</summary>
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<AdminPriceItem>> Update(Guid id, UpdatePriceRequest request, CancellationToken cancellationToken)
    {
        if (!Text(request.Name, true, out var name) || !Text(request.Description, false, out var description) ||
            !Decimal(request.Price, out var price) || !Bool(request.IsFrom, out var isFrom) || !Integer(request.SortOrder, out var sortOrder) ||
            !Bool(request.IsPublished, out var isPublished) || !Bool(request.IsArchived, out var isArchived) ||
            (request.Name.ValueKind == JsonValueKind.Undefined && request.Description.ValueKind == JsonValueKind.Undefined &&
             request.Price.ValueKind == JsonValueKind.Undefined && request.IsFrom.ValueKind == JsonValueKind.Undefined &&
             request.SortOrder.ValueKind == JsonValueKind.Undefined && request.IsPublished.ValueKind == JsonValueKind.Undefined && request.IsArchived.ValueKind == JsonValueKind.Undefined))
            return Invalid();
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actorId is null) return Unauthorized();
        var (result, item) = await prices.UpdateAsync(id, request.Version!.Value,
            new PriceChange(name, description, price, isFrom, sortOrder, isPublished, isArchived), actorId, cancellationToken);
        return result switch
        {
            PriceUpdateResult.Success => Ok(item),
            PriceUpdateResult.NotFound => NotFound(),
            PriceUpdateResult.Invalid => Invalid(),
            _ => Conflict(new ProblemDetails { Status = 409, Title = "Дані послуги змінилися. Оновіть список." })
        };
    }

    private static bool Text(JsonElement value, bool name, out string? result)
    {
        result = null;
        if (value.ValueKind == JsonValueKind.Undefined) return true;
        if (value.ValueKind != JsonValueKind.String) return false;
        result = value.GetString();
        return name ? PriceListItem.ValidName(result) : PriceListItem.ValidDescription(result);
    }
    private static bool Decimal(JsonElement value, out decimal? result)
    {
        result = null;
        if (value.ValueKind == JsonValueKind.Undefined) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) || number < 0) return false;
        result = number; return true;
    }
    private static bool Integer(JsonElement value, out int? result)
    {
        result = null;
        if (value.ValueKind == JsonValueKind.Undefined) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) return false;
        result = number; return true;
    }
    private static bool Bool(JsonElement value, out bool? result)
    {
        result = null;
        if (value.ValueKind == JsonValueKind.Undefined) return true;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        result = value.GetBoolean(); return true;
    }
    private BadRequestObjectResult Invalid() => BadRequest(new ProblemDetails { Status = 400, Title = "Перевірте дані послуги." });
}

/// <summary>Information for a new indicative service price.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreatePriceRequest
{
    [Required] public string? Name { get; init; }
    public string? Description { get; init; }
    [Required] public decimal? Price { get; init; }
    [Required] public bool? IsFrom { get; init; }
    [Required] public int? SortOrder { get; init; }
    [Required] public bool? IsPublished { get; init; }
}

/// <summary>Only supplied fields are changed. Version comes from an administrative response.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdatePriceRequest
{
    [Required] public uint? Version { get; init; }
    public JsonElement Name { get; init; }
    public JsonElement Description { get; init; }
    public JsonElement Price { get; init; }
    public JsonElement IsFrom { get; init; }
    public JsonElement SortOrder { get; init; }
    public JsonElement IsPublished { get; init; }
    public JsonElement IsArchived { get; init; }
}
