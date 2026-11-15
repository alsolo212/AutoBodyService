using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoBodyService.Application.Finance;
using AutoBodyService.Domain.Finance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "SuperAdmin")]
[Route("api/admin/reports")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminFinanceController(IFinanceReports finance) : ControllerBase
{
    /// <summary>Monthly completion-based totals and a page of included orders.</summary>
    [HttpGet("finance")]
    public async Task<ActionResult<FinanceReport>> Report([FromQuery] int? year, [FromQuery] int? month,
        [FromQuery] string sort = "date", [FromQuery] string direction = "desc", [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25, [FromQuery] DateOnly? fromDate = null, [FromQuery] DateOnly? toDate = null,
        [FromQuery] decimal? minAmount = null, [FromQuery] decimal? maxAmount = null, CancellationToken cancellationToken = default)
    {
        if (!ValidQuery(year, month, sort, direction, page, pageSize)) return Invalid();
        if ((fromDate.HasValue && (fromDate.Value.Year != year || fromDate.Value.Month != month)) ||
            (toDate.HasValue && (toDate.Value.Year != year || toDate.Value.Month != month)) ||
            fromDate > toDate || minAmount < 0 || maxAmount < 0 || minAmount > maxAmount) return Invalid();
        return Ok(await finance.ReportAsync(year!.Value, month!.Value, new(fromDate, toDate, minAmount, maxAmount), sort,
            direction == "desc", page, pageSize, cancellationToken));
    }

    /// <summary>Lists general expenses recorded in a selected Kyiv month.</summary>
    [HttpGet("expenses")]
    public async Task<ActionResult<ExpensePage>> Expenses([FromQuery] int? year, [FromQuery] int? month,
        [FromQuery] string sort = "date", [FromQuery] string direction = "desc", [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        if (!ValidQuery(year, month, sort, direction, page, pageSize)) return Invalid();
        return Ok(await finance.ExpensesAsync(year!.Value, month!.Value, sort, direction == "desc", page, pageSize, cancellationToken));
    }

    /// <summary>Records a general expense in the current Kyiv month.</summary>
    [HttpPost("expenses")]
    public async Task<ActionResult<ExpenseItem>> Create(CreateExpenseRequest request, CancellationToken cancellationToken)
    {
        if (request.Amount is null or <= 0 || !MonthlyExpense.ValidDescription(request.Description)) return Invalid();
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Unauthorized();
        var item = await finance.CreateAsync(request.Amount.Value, request.Description, actor, cancellationToken);
        return Created($"/api/admin/reports/expenses?year={item.RecordedYear}&month={item.RecordedMonth}", item);
    }

    /// <summary>Corrects selected fields without moving the expense to another month.</summary>
    [HttpPatch("expenses/{id:guid}")]
    public async Task<ActionResult<ExpenseItem>> Update(Guid id, UpdateExpenseRequest request, CancellationToken cancellationToken)
    {
        decimal? amount = null;
        if (request.Amount.ValueKind != JsonValueKind.Undefined)
        {
            if (request.Amount.ValueKind != JsonValueKind.Number || !request.Amount.TryGetDecimal(out var number) || number <= 0) return Invalid();
            amount = number;
        }
        var hasDescription = request.Description.ValueKind != JsonValueKind.Undefined;
        string? description = null;
        if (hasDescription)
        {
            if (request.Description.ValueKind == JsonValueKind.String) description = request.Description.GetString();
            else if (request.Description.ValueKind != JsonValueKind.Null) return Invalid();
            if (!MonthlyExpense.ValidDescription(description)) return Invalid();
        }
        if (amount is null && !hasDescription) return Invalid();
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null) return Unauthorized();
        var (result, item) = await finance.UpdateAsync(id, request.Version!.Value, new(amount, hasDescription, description), actor, cancellationToken);
        return result switch
        {
            ExpenseUpdateResult.Success => Ok(item),
            ExpenseUpdateResult.NotFound => NotFound(),
            _ => Conflict(new ProblemDetails { Status = 409, Title = "Витрату змінено. Оновіть список." })
        };
    }

    private static bool ValidQuery(int? year, int? month, string sort, string direction, int page, int pageSize) =>
        year is >= 2 and <= 9998 && month is >= 1 and <= 12 && sort is "date" or "amount" &&
        direction is "asc" or "desc" && page is >= 1 and <= 1_000_000 && pageSize is >= 1 and <= 100;
    private BadRequestObjectResult Invalid() => BadRequest(new ProblemDetails { Status = 400, Title = "Перевірте параметри звіту або витрати." });
}

/// <summary>Amount and optional description; the server assigns the recording time and month.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateExpenseRequest
{
    [Required] public decimal? Amount { get; init; }
    public string? Description { get; init; }
}

/// <summary>Current version and only the fields to correct.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateExpenseRequest
{
    [Required] public uint? Version { get; init; }
    public JsonElement Amount { get; init; }
    public JsonElement Description { get; init; }
}
