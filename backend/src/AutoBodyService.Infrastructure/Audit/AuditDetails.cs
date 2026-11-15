using System.Globalization;
using System.Text.Json;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Domain.InspectionSchedule;

namespace AutoBodyService.Infrastructure.Audit;

// Historical JSON is never returned verbatim. Only known action-specific fields and
// constrained values are allowed, so older or future free-text fields cannot leak.
internal static class AuditDetails
{
    public static IReadOnlyDictionary<string, object?> Safe(string action, string json)
    {
        using var document = JsonDocument.Parse(json);
        var keys = action switch
        {
            "AppointmentStatusChanged" => new[] { "from", "to", "statusEventId" },
            "RepairPlanChanged" => new[] { "priceChanged", "datesChanged" },
            "AccountPasswordReset" or "AccountAdminChanged" or "AccountBlockedChanged" => new[] { "value" },
            "AssignmentCreated" or "AssignmentReleased" => new[] { "appointmentId", "employeeId", "automatic" },
            "ExpenseCreated" or "ExpenseCorrected" => new[] { "before", "after", "descriptionChanged" },
            "InspectionScheduleChanged" or "InspectionExceptionChanged" => new[] { "before", "after" },
            "ManualContactRecorded" => new[] { "reason" },
            _ => Array.Empty<string>()
        };
        return Object(document.RootElement, keys, action);
    }

    private static Dictionary<string, object?> Object(JsonElement element, string[] keys, string action)
    {
        var result = new Dictionary<string, object?>();
        if (element.ValueKind != JsonValueKind.Object) return result;
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value)) continue;
            if (TryValue(key, value, action, out var safe)) result[key] = safe;
        }
        return result;
    }

    private static bool TryValue(string key, JsonElement value, string action, out object? result)
    {
        result = null;
        if (value.ValueKind == JsonValueKind.Null && key is "before" or "after" or "value" or "statusEventId") return true;
        if (key is "priceChanged" or "datesChanged" or "value" or "automatic" or "hasDescription" or "descriptionChanged")
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            result = value.GetBoolean(); return true;
        }
        if (key == "amount")
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var amount)) return false;
            result = amount; return true;
        }
        if (key == "DayOfWeek")
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var day) || day is < 0 or > 6) return false;
            result = day; return true;
        }
        if (key is "statusEventId" or "appointmentId" or "employeeId")
        {
            if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out var id)) return false;
            result = id; return true;
        }
        if (key is "Start" or "End")
        {
            if (value.ValueKind != JsonValueKind.String || !TimeOnly.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var time)) return false;
            result = time.ToString("HH:mm:ss", CultureInfo.InvariantCulture); return true;
        }
        if (key is "from" or "to" or "reason" or "mode")
        {
            if (value.ValueKind != JsonValueKind.String) return false;
            var text = value.GetString();
            var valid = key switch
            {
                "from" or "to" => Enum.TryParse<AppointmentStatus>(text, out var status) && Enum.IsDefined(status) && status.ToString() == text,
                "reason" => Enum.TryParse<ManualContactReason>(text, out var reason) && Enum.IsDefined(reason) && reason.ToString() == text,
                _ => (Enum.TryParse<DateOverrideMode>(text, out var dateMode) && Enum.IsDefined(dateMode) && dateMode.ToString() == text) ||
                     (Enum.TryParse<SlotOverrideMode>(text, out var slotMode) && Enum.IsDefined(slotMode) && slotMode.ToString() == text)
            };
            if (!valid) return false;
            result = text; return true;
        }
        if (key is "before" or "after")
        {
            if (action is "ExpenseCreated" or "ExpenseCorrected") result = Object(value, ["amount", "hasDescription"], action);
            else if (action == "InspectionExceptionChanged") result = Object(value, ["mode", "intervals", "slots"], action);
            else if (action == "InspectionScheduleChanged" && value.ValueKind == JsonValueKind.Array)
                result = value.EnumerateArray().Select(e => Object(e, ["DayOfWeek", "Start", "End"], action)).ToArray();
            else return false;
            return true;
        }
        if (key is "intervals" or "slots" && value.ValueKind == JsonValueKind.Array)
        {
            var allowed = key == "intervals" ? new[] { "Start", "End" } : new[] { "Start", "mode" };
            result = value.EnumerateArray().Select(e => Object(e, allowed, action)).ToArray(); return true;
        }
        return false;
    }
}
