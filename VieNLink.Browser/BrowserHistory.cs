using System.IO;

namespace VieNLink;

/// <summary>Запись истории посещений.</summary>
internal sealed class BrowserHistoryEntry
{
    public string Title { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public DateTime VisitedUtc { get; set; } = DateTime.UtcNow;

    public int Visits { get; set; } = 1;

    public string VisitedLabel => VisitedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
}

/// <summary>
/// История встроенного браузера: последние 3000 адресов, повторный заход обновляет запись.
/// Хранится в %LOCALAPPDATA%\VieNLink\browser-history.json.
/// </summary>
internal sealed class BrowserHistory
{
    private const int MaxEntries = 3000;
    private static readonly object Sync = new();

    public List<BrowserHistoryEntry> Items { get; set; } = new();

    public static BrowserHistory Load() =>
        JsonFileStore.Read<BrowserHistory>(AppPaths.BrowserHistoryPath) ?? new BrowserHistory();

    public static bool IsWebAddress(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
        !url.StartsWith("https://vienlink.start/", StringComparison.OrdinalIgnoreCase);

    public void Add(string? title, string? url)
    {
        if (!IsWebAddress(url))
        {
            return;
        }

        lock (Sync)
        {
            var last = Items.FirstOrDefault();
            if (last is not null &&
                last.Url.Equals(url, StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow - last.VisitedUtc < TimeSpan.FromMinutes(1))
            {
                last.VisitedUtc = DateTime.UtcNow;
                last.Visits++;
                if (!string.IsNullOrWhiteSpace(title))
                {
                    last.Title = title!;
                }
            }
            else
            {
                Items.Insert(0, new BrowserHistoryEntry
                {
                    Title = string.IsNullOrWhiteSpace(title) ? url! : title!,
                    Url = url!,
                    VisitedUtc = DateTime.UtcNow,
                });
                if (Items.Count > MaxEntries)
                {
                    Items.RemoveRange(MaxEntries, Items.Count - MaxEntries);
                }
            }
        }

        Save();
    }

    public void Remove(BrowserHistoryEntry entry)
    {
        Items.Remove(entry);
        Save();
    }

    public void Clear()
    {
        Items.Clear();
        Save();
    }

    public void Save() => JsonFileStore.Write(AppPaths.BrowserHistoryPath, this);
}
