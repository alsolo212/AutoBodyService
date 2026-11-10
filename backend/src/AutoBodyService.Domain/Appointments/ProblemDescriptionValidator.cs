using System.Text.RegularExpressions;

namespace AutoBodyService.Domain.Appointments;

/// <summary>Checks that links do not replace a written description of the problem.</summary>
public static partial class ProblemDescriptionValidator
{
    private static readonly HashSet<string> LinkPrompts = new(StringComparer.OrdinalIgnoreCase)
    {
        "дивіться", "дивись", "дивитися", "подивіться", "гляньте", "перегляньте", "переглянути", "відео", "фото", "зображення", "посилання", "посиланням", "за", "на", "по", "і", "й", "та", "ось", "тут", "це", "моє", "мій", "будь", "ласка",
        "смотрите", "смотри", "смотреть", "посмотрите", "гляньте", "видео", "фото", "изображение", "ссылка", "ссылке", "ссылки", "см", "и", "в", "вот", "здесь", "ниже", "это", "моё", "мое", "мой", "пожалуйста",
        "see", "watch", "view", "video", "photo", "image", "link", "the", "and", "this", "my", "url", "please", "here", "below"
    };

    public static bool IsValid(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        var hasLink = false;
        var remaining = Tokens().Replace(description, token =>
        {
            var prefix = LinkPrefix().Match(token.Value);
            if (!prefix.Success) return token.Value;
            var candidate = token.Value[prefix.Index..].TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '»', '”');
            if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) candidate = "https://" + candidate;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host))
                return token.Value;
            hasLink = true;
            return token.Value[..prefix.Index] + " ";
        });
        if (!hasLink) return true;
        return Words().Matches(remaining).Any(word => !LinkPrompts.Contains(word.Value));
    }

    // A URL extends to whitespace. Commas, parentheses and non-ASCII query values can
    // all belong to it; separate any following description with whitespace.
    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Tokens();

    [GeneratedRegex(@"(?:https?://|www\.|(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,63}(?=[^a-z0-9-]|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPrefix();

    [GeneratedRegex("[\\p{L}\\p{M}]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Words();
}
