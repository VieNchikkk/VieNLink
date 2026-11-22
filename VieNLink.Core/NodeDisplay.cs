using System.Text.RegularExpressions;

namespace VieNLink;

/// <summary>
/// Отображаемое имя сервера: убирает из названия Telegram-каналы, ссылки и служебные
/// метки источников. Само имя в модели не меняем — оно нужно для канала/источника.
/// </summary>
internal static class NodeDisplay
{
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TelegramLinkRegex = new(@"(?:t\.me/|telegram\.me/)\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HandleRegex = new(@"@[A-Za-z0-9_]{3,}", RegexOptions.Compiled);
    private static readonly Regex LabelRegex = new(@"\b(?:TG|Telegram|канал|channel)\s*:?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpacesRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex SeparatorRegex = new(@"[|·]", RegexOptions.Compiled);

    public static string Name(ProxyNode node)
    {
        var cleaned = CleanName(node.Name);
        if (cleaned.Length > 1)
        {
            return cleaned;
        }

        if (!string.IsNullOrWhiteSpace(node.Server))
        {
            return node.Server + ":" + node.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return string.IsNullOrWhiteSpace(node.Protocol) ? "Сервер" : node.Protocol;
    }

    public static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var text = UrlRegex.Replace(name, " ");
        text = TelegramLinkRegex.Replace(text, " ");
        text = HandleRegex.Replace(text, " ");
        text = LabelRegex.Replace(text, " ");
        text = SpacesRegex.Replace(text, " ").Trim();

        var parts = SeparatorRegex.Split(text)
            .Select(part => part.Trim(' ', '-', '—', '–', '…', ',', ';', ':', '.', '|', '·'))
            .Where(part => part.Length > 0 && part != "…")
            .ToList();
        text = string.Join(" · ", parts).Trim(' ', '-', '—', '–', '…', ',', ';', ':', '.', '|', '·');
        return SpacesRegex.Replace(text, " ").Trim();
    }
}
