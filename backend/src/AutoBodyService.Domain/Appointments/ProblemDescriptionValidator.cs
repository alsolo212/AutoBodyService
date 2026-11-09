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
        if (!Links().IsMatch(description)) return true;
        var remaining = Links().Replace(description, " ");
        return Words().Matches(remaining).Any(word => !LinkPrompts.Contains(word.Value));
    }

    // A link may use a scheme, www, or a bare domain. Separators stay outside the match.
    [GeneratedRegex("(?:https?://|www\\.)[^\\s<>\\\"'«»“”(),;!?–—]+(?:\\?[^\\s<>\\\"'«»“”()!–—]+)?|(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\\.)+[a-z]{2,63}(?:/[^\\s<>\\\"'«»“”(),;!?–—]+(?:\\?[^\\s<>\\\"'«»“”()!–—]+)?)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Links();

    [GeneratedRegex("[\\p{L}\\p{M}]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Words();
}
