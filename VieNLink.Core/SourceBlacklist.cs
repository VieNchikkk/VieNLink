using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VieNLink;

/// <summary>
/// Одна запись чёрного списка источников. Хранит сразу несколько ключей (URL, репозиторий,
/// «семейство» ежедневных URL), чтобы одна блокировка ловила и зеркала, и следующие
/// выпуски того же списка.
/// </summary>
internal sealed class SourceBlacklistEntry
{
    public string Key { get; set; } = string.Empty;
    public string Scope { get; set; } = "url";
    public string? Url { get; set; }
    public string? Repository { get; set; }
    public string? Family { get; set; }
    public string? ContentHash { get; set; }
    public string Reason { get; set; } = "manual";
    public string? Note { get; set; }
    public bool Manual { get; set; }
    public DateTime AddedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public int Nodes { get; set; }
    public int Working { get; set; }

    [JsonIgnore]
    public string ReasonText => Reason switch
    {
        "barren" => "бесплодный",
        "mirror" => "зеркало",
        "gone" => "источник пропал",
        "manual" => "вручную",
        _ => Reason,
    };

    [JsonIgnore]
    public string Display => (Repository ?? Url ?? Family ?? Key) + " · " + ReasonText +
                             (Manual
                                 ? string.Empty
                                 : ExpiresUtc is { } expires
                                     ? " · до " + expires.ToLocalTime().ToString("dd.MM")
                                     : string.Empty);

    public bool IsExpired(DateTime nowUtc) => ExpiresUtc is { } expires && nowUtc >= expires;
}

internal sealed class SourceBlacklistFile
{
    public int Version { get; set; } = 1;
    public DateTime UpdatedUtc { get; set; }
    public List<SourceBlacklistEntry> Entries { get; set; } = new();
}

/// <summary>
/// Чёрный список источников: URL и репозитории, которые запрещено добавлять.
/// Ручная блокировка — навсегда, автоматическая («бесплодный», «зеркало», «пропал») —
/// мягкая: через 30 дней источник снова может быть найден, и если он по-прежнему мёртв,
/// ротация заблокирует его снова. Это защищает от «оживших» репозиториев.
/// </summary>
internal static class SourceBlacklist
{
    private static readonly object Gate = new();
    private static readonly Regex DateInPath = new(@"\d{6,8}", RegexOptions.Compiled);
    private static readonly Regex IsoDateInPath = new(@"\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);
    private static List<SourceBlacklistEntry>? _entries;

    // Для SelfTest: не трогаем реальный файл пользователя.
    internal static string? PathOverride { get; set; }
    internal static DateTime? NowOverride { get; set; }

    private static string FilePath => PathOverride ?? AppPaths.SourceBlacklistPath;
    private static DateTime UtcNow => NowOverride ?? DateTime.UtcNow;

    public static IReadOnlyList<SourceBlacklistEntry> All()
    {
        lock (Gate)
        {
            var entries = EnsureLoaded();
            var now = UtcNow;
            if (Prune(entries, now))
            {
                SaveLocked(entries);
            }

            return entries.Select(Clone).ToList();
        }
    }

    /// <summary>Источник запрещён? «Мои» подписки не блокируются автоматически.</summary>
    public static bool IsBlocked(SourceDefinition source, out SourceBlacklistEntry? entry)
    {
        entry = null;
        if (source.IsMine || string.IsNullOrWhiteSpace(source.Url))
        {
            return false;
        }

        return IsBlockedUrl(source.Url, out entry);
    }

    /// <summary>Проверка по адресу — в том числе для ручного добавления источника.</summary>
    public static bool IsBlockedUrl(string url, out SourceBlacklistEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var canonical = CanonicalUrl(url);
        var repository = RepositoryKey(canonical);
        var family = FamilyKey(canonical);
        lock (Gate)
        {
            var entries = EnsureLoaded();
            var now = UtcNow;
            if (Prune(entries, now))
            {
                SaveLocked(entries);
            }

            foreach (var candidate in entries)
            {
                if (candidate.IsExpired(now))
                {
                    continue;
                }

                if (Matches(candidate, canonical, repository, family))
                {
                    entry = Clone(candidate);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Репозиторий запрещён? Нужен автопоиску, чтобы не качать его файлы.</summary>
    public static bool IsBlockedRepository(string host, string fullName, out SourceBlacklistEntry? entry)
    {
        entry = null;
        var repository = NormalizeRepository(host, fullName);
        if (repository.Length == 0)
        {
            return false;
        }

        lock (Gate)
        {
            var entries = EnsureLoaded();
            var now = UtcNow;
            foreach (var candidate in entries)
            {
                if (candidate.IsExpired(now))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(candidate.Repository) &&
                    candidate.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))
                {
                    entry = Clone(candidate);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Автоматическая блокировка: мягкая, по умолчанию на 30 дней.</summary>
    public static SourceBlacklistEntry BlockAuto(
        SourceDefinition source,
        string reason,
        string? note = null,
        TimeSpan? ttl = null,
        int? nodes = null,
        int? working = null,
        bool repositoryScope = true)
    {
        var url = CanonicalUrl(source.Url);
        var repository = repositoryScope ? RepositoryKey(url) : null;
        var family = FamilyKey(url);
        var scope = repository is not null ? "repo" : family is not null ? "family" : "url";
        var key = repository ?? family ?? url;
        var entry = new SourceBlacklistEntry
        {
            Key = key,
            Scope = scope,
            Url = string.IsNullOrEmpty(url) ? null : url,
            Repository = repository,
            Family = repository is null ? family : null,
            Reason = reason,
            Note = note,
            Manual = false,
            AddedUtc = UtcNow,
            ExpiresUtc = UtcNow + (ttl ?? TimeSpan.FromDays(30)),
            Nodes = nodes ?? source.TotalNodeCount,
            Working = working ?? source.TotalWorkingNodeCount,
        };
        return Upsert(entry, replaceManual: false);
    }

    /// <summary>Ручная блокировка: навсегда, пока пользователь не разблокирует.</summary>
    public static SourceBlacklistEntry BlockManual(SourceDefinition source, string? note = null)
    {
        var url = CanonicalUrl(source.Url);
        var repository = RepositoryKey(url);
        var entry = new SourceBlacklistEntry
        {
            Key = repository ?? url,
            Scope = repository is not null ? "repo" : "url",
            Url = string.IsNullOrEmpty(url) ? null : url,
            Repository = repository,
            Reason = "manual",
            Note = note ?? "удалён вручную",
            Manual = true,
            AddedUtc = UtcNow,
            ExpiresUtc = null,
            Nodes = source.TotalNodeCount,
            Working = source.TotalWorkingNodeCount,
        };
        return Upsert(entry, replaceManual: true);
    }

    /// <summary>Убрать источник из чёрного списка. Возвращает число снятых записей.</summary>
    public static int Unblock(SourceDefinition source)
    {
        var url = CanonicalUrl(source.Url);
        var repository = RepositoryKey(url);
        var family = FamilyKey(url);
        lock (Gate)
        {
            var entries = EnsureLoaded();
            var removed = entries.RemoveAll(entry =>
                Matches(entry, url, repository, family));
            if (removed > 0)
            {
                SaveLocked(entries);
            }

            return removed;
        }
    }

    public static bool UnblockKey(string key)
    {
        lock (Gate)
        {
            var entries = EnsureLoaded();
            var removed = entries.RemoveAll(entry =>
                entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveLocked(entries);
                return true;
            }

            return false;
        }
    }

    /// <summary>Нормализованный адрес: без зеркал, фрагмента и различий регистра хоста.</summary>
    public static string CanonicalUrl(string? url)
    {
        var value = (url ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        foreach (var prefix in new[]
                 {
                     "https://ghfast.top/", "http://ghfast.top/",
                     "https://gh-proxy.com/", "http://gh-proxy.com/",
                 })
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = value[prefix.Length..].TrimStart('/');
            if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                value = "https://" + value;
            }

            break;
        }

        var hash = value.IndexOf('#');
        if (hash >= 0)
        {
            value = value[..hash];
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value.ToLowerInvariant();
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var canonical = uri.Scheme.ToLowerInvariant() + "://" + uri.Host.ToLowerInvariant() + path;
        if (!string.IsNullOrEmpty(uri.Query))
        {
            canonical += uri.Query;
        }

        return canonical;
    }

    /// <summary>Ключ репозитория: github:owner/repo, gitlab:owner/repo, gitee:owner/repo.</summary>
    public static string? RepositoryKey(string? url)
    {
        if (!Uri.TryCreate(url ?? string.Empty, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return host switch
        {
            "raw.githubusercontent.com" when segments.Length >= 2 =>
                NormalizeRepository("github", segments[0] + "/" + segments[1]),
            "github.com" when segments.Length >= 2 =>
                NormalizeRepository("github", segments[0] + "/" + segments[1]),
            "gitlab.com" when segments.Length >= 2 =>
                NormalizeRepository("gitlab", segments[0] + "/" + segments[1]),
            "gitee.com" when segments.Length >= 2 =>
                NormalizeRepository("gitee", segments[0] + "/" + segments[1]),
            _ => null,
        };
    }

    public static string NormalizeRepository(string host, string fullName)
    {
        var normalizedHost = host.ToLowerInvariant() switch
        {
            "raw.githubusercontent.com" => "github",
            _ => host.ToLowerInvariant(),
        };
        return normalizedHost + ":" + fullName.Trim().Trim('/').ToLowerInvariant();
    }

    /// <summary>
    /// «Семейство» ежедневных URL: дата в пути заменяется на {date}, чтобы блокировка
    /// вчерашнего файла ловила сегодняшний. Для GitHub-ссылок семейство не нужно —
    /// там работает блокировка репозитория.
    /// </summary>
    public static string? FamilyKey(string? url)
    {
        if (!Uri.TryCreate(url ?? string.Empty, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (RepositoryKey(url) is not null)
        {
            return null;
        }

        var path = IsoDateInPath.Replace(uri.AbsolutePath, "{date}");
        path = DateInPath.Replace(path, "{date}");
        return "family:" + uri.Host.ToLowerInvariant() + path.TrimEnd('/');
    }

    /// <summary>Хеш набора ссылок: для блокировки зеркал с другим адресом, но тем же содержимым.</summary>
    public static string ContentHash(IEnumerable<ProxyNode> nodes)
    {
        var lines = nodes
            .Select(node => node.RawLink ?? string.Empty)
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(line => line, StringComparer.Ordinal);
        var payload = string.Join('\n', lines);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    internal static void ResetForTests(string? path = null, DateTime? now = null)
    {
        lock (Gate)
        {
            PathOverride = path;
            NowOverride = now;
            _entries = null;
        }
    }

    private static SourceBlacklistEntry Upsert(SourceBlacklistEntry entry, bool replaceManual)
    {
        lock (Gate)
        {
            var entries = EnsureLoaded();
            var current = entries.FirstOrDefault(existing =>
                existing.Key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(existing.Url) && !string.IsNullOrEmpty(entry.Url) &&
                 existing.Url.Equals(entry.Url, StringComparison.OrdinalIgnoreCase)));
            if (current is not null)
            {
                if (current.Manual && !replaceManual)
                {
                    // Ручную блокировку автоматика не ослабляет и не продлевает.
                    return Clone(current);
                }

                current.Reason = entry.Reason;
                current.Note = entry.Note;
                current.Manual = entry.Manual;
                current.AddedUtc = entry.AddedUtc;
                current.ExpiresUtc = entry.ExpiresUtc;
                current.Nodes = Math.Max(current.Nodes, entry.Nodes);
                current.Working = Math.Max(current.Working, entry.Working);
                current.Url ??= entry.Url;
                current.Repository ??= entry.Repository;
                current.Family ??= entry.Family;
                SaveLocked(entries);
                return Clone(current);
            }

            entries.Add(entry);
            SaveLocked(entries);
            return Clone(entry);
        }
    }

    private static bool Matches(
        SourceBlacklistEntry entry,
        string url,
        string? repository,
        string? family)
    {
        if (!string.IsNullOrEmpty(entry.Url) &&
            entry.Url.Equals(url, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(entry.Repository) && !string.IsNullOrEmpty(repository) &&
            entry.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrEmpty(entry.Family) && !string.IsNullOrEmpty(family) &&
               entry.Family.Equals(family, StringComparison.OrdinalIgnoreCase);
    }

    private static List<SourceBlacklistEntry> EnsureLoaded()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        var file = JsonFileStore.Read<SourceBlacklistFile>(FilePath);
        _entries = file?.Entries ?? new List<SourceBlacklistEntry>();
        return _entries;
    }

    private static bool Prune(List<SourceBlacklistEntry> entries, DateTime nowUtc)
    {
        var removed = entries.RemoveAll(entry => entry.IsExpired(nowUtc));
        return removed > 0;
    }

    private static void SaveLocked(List<SourceBlacklistEntry> entries)
    {
        entries.Sort((left, right) => right.AddedUtc.CompareTo(left.AddedUtc));
        JsonFileStore.WriteCompact(FilePath, new SourceBlacklistFile
        {
            Version = 1,
            UpdatedUtc = UtcNow,
            Entries = entries,
        });
    }

    private static SourceBlacklistEntry Clone(SourceBlacklistEntry entry) => new()
    {
        Key = entry.Key,
        Scope = entry.Scope,
        Url = entry.Url,
        Repository = entry.Repository,
        Family = entry.Family,
        ContentHash = entry.ContentHash,
        Reason = entry.Reason,
        Note = entry.Note,
        Manual = entry.Manual,
        AddedUtc = entry.AddedUtc,
        ExpiresUtc = entry.ExpiresUtc,
        Nodes = entry.Nodes,
        Working = entry.Working,
    };
}
