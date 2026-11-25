namespace VieNLink;

/// <summary>Полный удачный конфиг обхода: режим, стратегия и флаги, с которыми он сработал.</summary>
internal sealed class BypassProfile
{
    /// <summary>zapret, desync, tun или tun-desync (TUN-обход через WinDivert).</summary>
    public string Mode { get; set; } = string.Empty;

    public int StrategyIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool GameMode { get; set; }
    public bool BlockQuic { get; set; }

    /// <summary>0–100: чем выше, тем лучше было подключение.</summary>
    public int Quality { get; set; }

    public int Ok { get; set; }
    public int Total { get; set; }
    public bool YoutubeOk { get; set; }
    public bool TelegramOk { get; set; }
    public bool DiscordOk { get; set; }
    public bool VideoOk { get; set; }
    public int AverageMs { get; set; }
    public int Uses { get; set; }
    public DateTime LastSuccessUtc { get; set; }

    /// <summary>
    /// Сети, на которых конфиг реально работал: «wifi:&lt;SSID&gt;», «eth:&lt;адаптер&gt;»,
    /// «cell:&lt;MCCMNC&gt;». На них он пробуется первым: дома, на работе и на раздаче
    /// с телефона рабочие стратегии разные.
    /// </summary>
    public List<string> Networks { get; set; } = new();
}

internal sealed class BypassProfileCache
{
    public int SchemaVersion { get; set; } = 1;
    public List<BypassProfile> Profiles { get; set; } = new();
}

/// <summary>
/// Память удачных конфигов обходов. Лучшие по качеству пробуются первыми;
/// если записей нет — приложение начинает с обычного выбора/автоподбора.
/// </summary>
internal static class BypassProfileStore
{
    private static readonly object Sync = new();
    private static BypassProfileCache? _cache;

    public static IReadOnlyList<BypassProfile> Ranked(string mode) => Ranked(mode, null);

    /// <summary>
    /// Лучшие конфиги режима. Сначала те, что уже работали на этой сети, затем по качеству.
    /// </summary>
    public static IReadOnlyList<BypassProfile> Ranked(string mode, string? network)
    {
        lock (Sync)
        {
            return LoadCore().Profiles
                .Where(profile => profile.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(profile => Fits(profile, network))
                .ThenByDescending(profile => profile.Quality)
                .ThenByDescending(profile => profile.LastSuccessUtc)
                .ThenByDescending(profile => profile.Uses)
                .Take(20)
                .ToList();
        }
    }

    /// <summary>Конфиг уже работал на этой сети.</summary>
    public static bool Fits(BypassProfile profile, string? network) =>
        !string.IsNullOrWhiteSpace(network) &&
        profile.Networks is not null &&
        profile.Networks.Contains(network, StringComparer.OrdinalIgnoreCase);

    public static void Record(BypassProfile profile) => RecordMany(new[] { profile });

    public static void RecordMany(IEnumerable<BypassProfile> profiles)
    {
        lock (Sync)
        {
            var cache = LoadCore();
            foreach (var profile in profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.Mode) || string.IsNullOrWhiteSpace(profile.Name))
                {
                    continue;
                }

                var existing = cache.Profiles.FirstOrDefault(candidate =>
                    candidate.Mode.Equals(profile.Mode, StringComparison.OrdinalIgnoreCase) &&
                    candidate.StrategyIndex == profile.StrategyIndex &&
                    candidate.GameMode == profile.GameMode &&
                    candidate.BlockQuic == profile.BlockQuic);
                if (existing is null)
                {
                    profile.LastSuccessUtc = DateTime.UtcNow;
                    profile.Uses = 1;
                    cache.Profiles.Add(profile);
                    continue;
                }

                existing.Name = profile.Name;
                // Сохраняем лучший известный результат конфига, а не последний случайный.
                existing.Quality = Math.Max(existing.Quality, profile.Quality);
                existing.Ok = Math.Max(existing.Ok, profile.Ok);
                existing.Total = Math.Max(existing.Total, profile.Total);
                existing.YoutubeOk |= profile.YoutubeOk;
                existing.TelegramOk |= profile.TelegramOk;
                existing.DiscordOk |= profile.DiscordOk;
                existing.VideoOk |= profile.VideoOk;
                existing.AverageMs = existing.AverageMs <= 0
                    ? profile.AverageMs
                    : profile.AverageMs <= 0
                        ? existing.AverageMs
                        : Math.Min(existing.AverageMs, profile.AverageMs);
                existing.Uses++;
                existing.LastSuccessUtc = DateTime.UtcNow;
                if (profile.Networks is { Count: > 0 })
                {
                    existing.Networks ??= new List<string>();
                    foreach (var network in profile.Networks)
                    {
                        if (string.IsNullOrWhiteSpace(network) ||
                            existing.Networks.Contains(network, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        existing.Networks.Add(network);
                        while (existing.Networks.Count > 8)
                        {
                            existing.Networks.RemoveAt(0);
                        }
                    }
                }
            }

            cache.Profiles = cache.Profiles
                .OrderByDescending(profile => profile.Quality)
                .ThenByDescending(profile => profile.LastSuccessUtc)
                .Take(200)
                .ToList();
            JsonFileStore.Write(AppPaths.BypassProfilesPath, cache);
        }
    }

    /// <summary>Оценка 0–100 по доле успешных проверок, доступным сервисам и задержке.</summary>
    public static int Score(
        int ok,
        int total,
        int averageMs,
        bool youtube,
        bool telegram,
        bool video,
        bool discord)
    {
        var rate = total > 0 ? (int)Math.Round(ok * 100.0 / total) : 0;
        var services = (youtube ? 25 : 0) + (telegram ? 20 : 0) + (video ? 15 : 0) + (discord ? 10 : 0);
        var latency = averageMs <= 0 ? 0 : Math.Max(0, 20 - averageMs / 50);
        return Math.Clamp(rate + services + latency, 0, 100);
    }

    private static BypassProfileCache LoadCore()
    {
        _cache ??= JsonFileStore.Read<BypassProfileCache>(AppPaths.BypassProfilesPath) ?? new BypassProfileCache();
        return _cache;
    }
}
