using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoBodyService.Application.Employees;
using AutoBodyService.Domain.Employees;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoBodyService.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/employees")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminEmployeesController(IEmployeeDirectory employees) : ControllerBase
{
    /// <summary>Returns active and inactive workers.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EmployeeDetails>>> List(CancellationToken cancellationToken) =>
        Ok(await employees.ListAsync(cancellationToken));

    /// <summary>Returns one worker.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EmployeeDetails>> Get(Guid id, CancellationToken cancellationToken)
    {
        var employee = await employees.GetAsync(id, cancellationToken);
        return employee is null ? NotFound() : Ok(employee);
    }

    /// <summary>Creates a worker without an authentication account.</summary>
    [HttpPost]
    public async Task<ActionResult<EmployeeDetails>> Create(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        if (!Employee.IsValidText(request.FullName) || !Employee.IsValidText(request.Specialization)) return Invalid();
        var employee = await employees.CreateAsync(request.FullName!, request.Specialization!, request.IsActive!.Value, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = employee.Id }, employee);
    }

    /// <summary>Updates supplied fields if the directory entry has not changed.</summary>
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<EmployeeDetails>> Update(Guid id, UpdateEmployeeRequest request, CancellationToken cancellationToken)
    {
        if (!TryText(request.FullName, out var name) || !TryText(request.Specialization, out var specialization) ||
            request.IsActive.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False) ||
            (request.FullName.ValueKind == JsonValueKind.Undefined && request.Specialization.ValueKind == JsonValueKind.Undefined && request.IsActive.ValueKind == JsonValueKind.Undefined))
            return Invalid();
        bool? active = request.IsActive.ValueKind == JsonValueKind.Undefined ? null : request.IsActive.GetBoolean();
        var (result, employee) = await employees.UpdateAsync(id, request.Version!.Value, new(name, specialization, active), cancellationToken);
        return result switch
        {
            EmployeeUpdateResult.Success => Ok(employee),
            EmployeeUpdateResult.NotFound => NotFound(),
            _ => Conflict(new ProblemDetails { Status = 409, Title = "Дані працівника змінилися. Оновіть сторінку." })
        };
    }

    private static bool TryText(JsonElement value, out string? text)
    {
        text = null;
        if (value.ValueKind == JsonValueKind.Undefined) return true;
        if (value.ValueKind != JsonValueKind.String) return false;
        text = value.GetString();
        return Employee.IsValidText(text);
    }

    private BadRequestObjectResult Invalid() => BadRequest(new ProblemDetails { Status = 400, Title = "Перевірте дані працівника." });
}

/// <summary>Information needed to create a worker.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateEmployeeRequest
{
    [Required] public string? FullName { get; init; }
    [Required] public string? Specialization { get; init; }
    [Required] public bool? IsActive { get; init; }
}

/// <summary>Only supplied fields are changed; version is obtained from a directory response.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateEmployeeRequest
{
    [Required] public uint? Version { get; init; }
    public JsonElement FullName { get; init; }
    public JsonElement Specialization { get; init; }
    public JsonElement IsActive { get; init; }
}
