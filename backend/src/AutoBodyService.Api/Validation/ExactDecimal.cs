using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace AutoBodyService.Api.Validation;

/// <summary>Checks the original numeric token before constructing a nonnegative decimal.</summary>
internal static class ExactDecimal
{
    public static bool TryOptionalQuery(string? input, out decimal? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(input)) return true;
        try
        {
            using var document = JsonDocument.Parse(input);
            if (!TryNonnegative(document.RootElement, out var parsed)) return false;
            value = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static bool TryNonnegative(JsonElement input, out decimal value)
    {
        value = 0;
        if (input.ValueKind != JsonValueKind.Number) return false;
        var raw = input.GetRawText();
        var negative = raw.StartsWith('-');
        if (negative) raw = raw[1..];
        var exponentIndex = raw.IndexOfAny(['e', 'E']);
        var exponent = 0;
        if (exponentIndex >= 0 && !int.TryParse(raw.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent)) return false;
        var mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
        var dot = mantissa.IndexOf('.');
        long scale = (dot < 0 ? 0 : mantissa.Length - dot - 1) - (long)exponent;
        var digits = (dot < 0 ? mantissa : mantissa.Remove(dot, 1)).TrimStart('0');
        var significant = digits.TrimEnd('0');
        scale -= digits.Length - significant.Length;
        if (significant.Length == 0) return true;
        if (negative || scale > 28 || significant.Length > 29 || (scale < 0 && significant.Length - scale > 29)) return false;

        var coefficient = BigInteger.Parse(significant, NumberStyles.None, CultureInfo.InvariantCulture);
        if (scale < 0) { coefficient *= BigInteger.Pow(10, (int)-scale); scale = 0; }
        if (coefficient > (BigInteger)decimal.MaxValue) return false;
        var lo = unchecked((int)(uint)(coefficient & uint.MaxValue));
        var mid = unchecked((int)(uint)((coefficient >> 32) & uint.MaxValue));
        var hi = unchecked((int)(uint)((coefficient >> 64) & uint.MaxValue));
        value = new decimal(lo, mid, hi, false, (byte)scale);
        return true;
    }
}
