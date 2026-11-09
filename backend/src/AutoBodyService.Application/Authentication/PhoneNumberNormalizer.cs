namespace AutoBodyService.Application.Authentication;

public static class PhoneNumberNormalizer
{
    public static string? Normalize(string value)
    {
        if (value.Length > 32 || value.Any(c => !char.IsAsciiDigit(c) && c is not ('+' or ' ' or '-' or '(' or ')')))
            return null;
        var compact = new string(value.Where(c => c is not (' ' or '-' or '(' or ')')).ToArray());
        var digits = compact switch
        {
            ['+', '3', '8', '0', ..] => compact[1..],
            ['3', '8', '0', ..] => compact,
            ['0', ..] when compact.Length == 10 => "38" + compact,
            _ => ""
        };
        return digits.Length == 12 && digits.StartsWith("380", StringComparison.Ordinal) && digits.All(char.IsAsciiDigit)
            ? "+" + digits : null;
    }
}
