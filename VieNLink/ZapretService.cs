using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace VieNLink;

/// <summary>Именованная стратегия zapret.</summary>
internal sealed record ZapretPreset(string Name, IReadOnlyList<string> Arguments)
{
    public ZapretPreset(string name, params string[] arguments) : this(name, (IReadOnlyList<string>)arguments)
    {
    }
}

/// <summary>Результат проверки одной стратегии при автоподборе.</summary>
internal sealed record ZapretProbeResult(
    int Index,
    string Name,
    int Ok,
    int Total,
    int AverageMs,
    bool YoutubeOk = false,
    bool TelegramOk = false,
    bool DiscordOk = false,
    bool VideoOk = false);

/// <summary>
/// Накопленная статистика по стратегиям: какие чаще открывали YouTube и работали полностью.
/// Хранится в %LOCALAPPDATA%\VieNLink\zapret-history.json и задаёт порядок перебора.
/// </summary>
internal sealed class ZapretHistoryEntry
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Runs { get; set; }
    public int YoutubeWins { get; set; }
    public int FullWins { get; set; }
    public double TotalMs { get; set; }
    public DateTime LastRunUtc { get; set; }

    public double YoutubeRate => Runs == 0 ? 0 : (double)YoutubeWins / Runs;
    public double FullRate => Runs == 0 ? 0 : (double)FullWins / Runs;
    public double AverageMs => Runs == 0 ? 0 : TotalMs / Runs;
}

/// <summary>
/// Обход DPI через zapret (winws + WinDivert). Работает на уровне пакетов Windows и не требует
/// прокси-сервера: разрезает/подменяет TLS ClientHello так, чтобы ТСПУ не увидел SNI, а сервер собрал поток.
/// Нужны права администратора и установленный драйвер WinDivert.
/// </summary>
internal sealed class ZapretService : IDisposable
{
    private const string RepoBase =
        "https://raw.githubusercontent.com/bol-van/zapret-win-bundle/master/";

    private static readonly Dictionary<string, string> FileUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winws.exe"] = "zapret-winws/winws.exe",
        ["WinDivert.dll"] = "zapret-winws/WinDivert.dll",
        ["WinDivert64.sys"] = "zapret-winws/WinDivert64.sys",
        ["cygwin1.dll"] = "zapret-winws/cygwin1.dll",
        [@"files\quic_initial_www_google_com.bin"] = "zapret-winws/files/quic_initial_www_google_com.bin",
        [@"files\stun.bin"] = "blockcheck/zapret/files/fake/stun.bin",
        [@"files\wireguard_initiation.bin"] = "blockcheck/zapret/files/fake/wireguard_initiation.bin",
        [@"files\list-youtube.txt"] = "zapret-winws/files/list-youtube.txt",
        [@"windivert.filter\windivert_part.discord_media.txt"] = "zapret-winws/windivert.filter/windivert_part.discord_media.txt",
        [@"windivert.filter\windivert_part.stun.txt"] = "zapret-winws/windivert.filter/windivert_part.stun.txt",
        [@"windivert.filter\windivert_part.wireguard.txt"] = "zapret-winws/windivert.filter/windivert_part.wireguard.txt",
        [@"windivert.filter\windivert_part.quic_initial_ietf.txt"] = "zapret-winws/windivert.filter/windivert_part.quic_initial_ietf.txt",
    };

    private static readonly string[] RequiredFiles = FileUrls.Keys.ToArray();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private const string QuicBlockRuleName = "VieNLinkBlockQuicUdp443";

    private static string? _directory;

    private Process? _process;
    private bool _quicBlocked;

    public static string Directory => _directory ??= ResolveDirectory();

    public static string ExecutablePath => Path.Combine(Directory, "winws.exe");

    /// <summary>
    /// Папка zapret: сначала рядом с программой (портативно), иначе в профиле пользователя.
    /// Рядом с exe запись может быть запрещена (Program Files, сеть, только чтение) — тогда откат
    /// в %LOCALAPPDATA%\VieNLink\zapret.
    /// </summary>
    private static string ResolveDirectory()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "zapret");
        if (IsInstalledIn(portable) || CanWrite(portable))
        {
            return portable;
        }

        return Path.Combine(AppPaths.DataDirectory, "zapret");
    }

    private static bool IsInstalledIn(string directory) =>
        RequiredFiles.All(file => IsUsableFile(Path.Combine(directory, file)));

    private static bool CanWrite(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-test");
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsUsableFile(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    /// <summary>
    /// Стратегии zapret: по одной на каждый режим десинка winws и типовые комбинации.
    /// Автоподбор перебирает их все и выбирает рабочую для YouTube (страница + видео),
    /// затем — с наибольшим числом открывшихся сервисов и лучшей задержкой.
    /// </summary>
    public static IReadOnlyList<ZapretPreset> Presets { get; } = new List<ZapretPreset>
    {
        // Официальный пресет из zapret-win-bundle (preset1_example.cmd), один в один:
        // TCP 80/443, hostlist YouTube, QUIC/STUN/WireGuard/Discord через raw-part фильтры.
        new("официальный zapret (win-bundle)",
            "--wf-tcp=80,443",
            @"--wf-raw-part=@windivert.filter\windivert_part.discord_media.txt",
            @"--wf-raw-part=@windivert.filter\windivert_part.stun.txt",
            @"--wf-raw-part=@windivert.filter\windivert_part.wireguard.txt",
            @"--wf-raw-part=@windivert.filter\windivert_part.quic_initial_ietf.txt",
            "--filter-tcp=80", "--dpi-desync=fake,fakedsplit", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig",
            "--new",
            "--filter-tcp=443", @"--hostlist=files\list-youtube.txt", "--dpi-desync=fake,multidisorder",
            "--dpi-desync-split-pos=1,midsld", "--dpi-desync-repeats=11", "--dpi-desync-fooling=md5sig",
            "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com",
            "--new",
            "--filter-tcp=443", "--dpi-desync=fake,multidisorder", "--dpi-desync-split-pos=midsld",
            "--dpi-desync-repeats=6", "--dpi-desync-fooling=badseq,md5sig",
            "--new",
            "--filter-l7=quic", @"--hostlist=files\list-youtube.txt", "--dpi-desync=fake",
            "--dpi-desync-repeats=11", @"--dpi-desync-fake-quic=files\quic_initial_www_google_com.bin",
            "--new",
            "--filter-l7=quic", "--dpi-desync=fake", "--dpi-desync-repeats=11"),

        new("fake + multidisorder",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fake,fakedsplit", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig",
            "--new",
            "--filter-tcp=443", "--dpi-desync=fake,multidisorder", "--dpi-desync-split-pos=1,midsld",
            "--dpi-desync-repeats=11", "--dpi-desync-fooling=md5sig",
            "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com",
            "--new",
            "--filter-tcp=443", "--dpi-desync=fake,multidisorder", "--dpi-desync-split-pos=midsld",
            "--dpi-desync-repeats=6", "--dpi-desync-fooling=badseq,md5sig"),

        new("fake + fakedsplit",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fake,fakedsplit", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig",
            "--new",
            "--filter-tcp=443", "--dpi-desync=fake,fakedsplit", "--dpi-desync-autottl=2",
            "--dpi-desync-repeats=6", "--dpi-desync-fooling=md5sig",
            "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com"),

        new("split2",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=split2", "--dpi-desync-split-pos=1",
            "--new",
            "--filter-tcp=443", "--dpi-desync=split2", "--dpi-desync-split-pos=1", "--dpi-desync-fooling=md5sig"),

        new("disorder",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=disorder", "--dpi-desync-split-pos=1,midsld",
            "--new",
            "--filter-tcp=443", "--dpi-desync=disorder", "--dpi-desync-split-pos=1,midsld"),

        new("fake + split2 + badseq",
            "--wf-tcp=80,443",
            "--filter-tcp=443", "--dpi-desync=fake,split2", "--dpi-desync-split-pos=1",
            "--dpi-desync-repeats=6", "--dpi-desync-fooling=badseq,md5sig",
            "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com"),

        new("fake + multidisorder (sniext)",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fake,fakedsplit", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig",
            "--new",
            "--filter-tcp=443", "--dpi-desync=fake,multidisorder", "--dpi-desync-split-pos=1,sniext+1",
            "--dpi-desync-repeats=6", "--dpi-desync-fooling=md5sig",
            "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com"),

        new("multidisorder",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=multidisorder", "--dpi-desync-split-pos=1",
            "--new",
            "--filter-tcp=443", "--dpi-desync=multidisorder", "--dpi-desync-split-pos=1,sniext+1",
            "--dpi-desync-repeats=6", "--dpi-desync-fooling=md5sig"),

        new("multisplit",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=multisplit", "--dpi-desync-split-pos=1",
            "--new",
            "--filter-tcp=443", "--dpi-desync=multisplit", "--dpi-desync-split-pos=1,sniext+1",
            "--dpi-desync-fooling=md5sig"),

        new("fake + multisplit",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fake,fakedsplit", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig",
            "--new",
            "--filter-tcp=443", "--dpi-desync=fake,multisplit", "--dpi-desync-split-pos=1,sniext+1",
            "--dpi-desync-repeats=6", "--dpi-desync-fooling=md5sig",
            "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com"),

        new("split2 (pos=2)",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=split2", "--dpi-desync-split-pos=1",
            "--new",
            "--filter-tcp=443", "--dpi-desync=split2", "--dpi-desync-split-pos=2", "--dpi-desync-fooling=md5sig"),

        new("split2 (midsld)",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=split2", "--dpi-desync-split-pos=1",
            "--new",
            "--filter-tcp=443", "--dpi-desync=split2", "--dpi-desync-split-pos=midsld", "--dpi-desync-fooling=md5sig"),

        new("split2 (sniext)",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=split2", "--dpi-desync-split-pos=1",
            "--new",
            "--filter-tcp=443", "--dpi-desync=split2", "--dpi-desync-split-pos=sniext", "--dpi-desync-fooling=md5sig"),

        new("fakeddisorder",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fakeddisorder", "--dpi-desync-split-pos=1",
            "--new",
            "--filter-tcp=443", "--dpi-desync=fakeddisorder", "--dpi-desync-split-pos=1",
            "--dpi-desync-autottl=2", "--dpi-desync-repeats=6", "--dpi-desync-fooling=md5sig"),

        new("fakedsplit",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fakedsplit", "--dpi-desync-split-pos=1",
            "--new",
            "--filter-tcp=443", "--dpi-desync=fakedsplit", "--dpi-desync-split-pos=1",
            "--dpi-desync-autottl=2", "--dpi-desync-repeats=6", "--dpi-desync-fooling=md5sig"),

        new("hostfakesplit",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fake,fakedsplit", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig",
            "--new",
            "--filter-tcp=443", "--dpi-desync=hostfakesplit", "--dpi-desync-hostfakesplit-midhost=sniext+1",
            "--dpi-desync-repeats=6", "--dpi-desync-fooling=md5sig"),

        new("fake (без split)",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fake", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig",
            "--new",
            "--filter-tcp=443", "--dpi-desync=fake", "--dpi-desync-repeats=6", "--dpi-desync-fooling=md5sig",
            "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com"),

        new("fakeknown",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fakeknown", "--dpi-desync-autottl=2",
            "--new",
            "--filter-tcp=443", "--dpi-desync=fakeknown", "--dpi-desync-repeats=6"),

        new("ipfrag1",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=ipfrag1",
            "--new",
            "--filter-tcp=443", "--dpi-desync=ipfrag1"),

        new("ipfrag2",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=ipfrag2", "--dpi-desync-ipfrag-pos-tcp=32",
            "--new",
            "--filter-tcp=443", "--dpi-desync=ipfrag2", "--dpi-desync-ipfrag-pos-tcp=32"),

        new("hopbyhop",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=hopbyhop",
            "--new",
            "--filter-tcp=443", "--dpi-desync=hopbyhop"),

        new("destopt",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=destopt",
            "--new",
            "--filter-tcp=443", "--dpi-desync=destopt"),

        new("synack",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fake,fakedsplit", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig",
            "--new",
            "--filter-tcp=443", "--dpi-desync=synack", "--dpi-desync-repeats=6"),

        new("syndata",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fake,fakedsplit", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig",
            "--new",
            "--filter-tcp=443", "--dpi-desync=syndata"),

        new("rst",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fake,fakedsplit", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig",
            "--new",
            "--filter-tcp=443", "--dpi-desync=rst", "--dpi-desync-repeats=6"),

        new("rstack",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fake,fakedsplit", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig",
            "--new",
            "--filter-tcp=443", "--dpi-desync=rstack", "--dpi-desync-repeats=6"),

        new("tamper",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fake,fakedsplit", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig",
            "--new",
            "--filter-tcp=443", "--dpi-desync=tamper"),

        new("split2 + wssize",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=split2", "--dpi-desync-split-pos=1",
            "--new",
            "--filter-tcp=443", "--dpi-desync=split2", "--dpi-desync-split-pos=1",
            "--dpi-desync-fooling=md5sig", "--wssize=1:6"),

        new("fake + fakedsplit + wssize",
            "--wf-tcp=80,443",
            "--filter-tcp=80", "--dpi-desync=fake,fakedsplit", "--dpi-desync-autottl=2", "--dpi-desync-fooling=md5sig",
            "--new",
            "--filter-tcp=443", "--dpi-desync=fake,fakedsplit", "--dpi-desync-autottl=2",
            "--dpi-desync-repeats=6", "--dpi-desync-fooling=md5sig",
            "--dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com", "--wssize=1:6"),
    };

    private static readonly string[] ProbeUrls =
    {
        "https://www.youtube.com/generate_204",
        "https://redirector.googlevideo.com/generate_204",
        "https://t.me/",
        "https://discord.com/",
    };

    public static bool IsInstalled => IsInstalledIn(Directory);

    /// <summary>Статистика по стратегиям из %LOCALAPPDATA%\VieNLink\zapret-history.json.</summary>
    public static IReadOnlyList<ZapretHistoryEntry> LoadHistory()
    {
        try
        {
            if (!File.Exists(AppPaths.ZapretHistoryPath))
            {
                return Array.Empty<ZapretHistoryEntry>();
            }

            var json = File.ReadAllText(AppPaths.ZapretHistoryPath);
            var entries = JsonSerializer.Deserialize<List<ZapretHistoryEntry>>(json) ?? new List<ZapretHistoryEntry>();
            // После изменения списка стратегий старые записи по индексам могут указывать на другие
            // пресеты — оставляем только совпадающие по имени.
            entries.RemoveAll(entry => entry.Index < 0 || entry.Index >= Presets.Count ||
                                       !string.Equals(Presets[entry.Index].Name, entry.Name, StringComparison.Ordinal));
            return entries;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Write("zapret", "Не удалось прочитать историю стратегий", exception);
            return Array.Empty<ZapretHistoryEntry>();
        }
    }

    /// <summary>
    /// Порядок перебора: сначала стратегии, которые чаще открывали YouTube и работали полностью,
    /// затем самые быстрые. Так повторный автоподбор находит рабочую стратегию быстрее.
    /// </summary>
    public static IReadOnlyList<int> GetHistoryOrder()
    {
        var history = LoadHistory();
        ZapretHistoryEntry? Entry(int index) => history.FirstOrDefault(item => item.Index == index);

        return Enumerable.Range(0, Presets.Count)
            .OrderByDescending(index => Entry(index)?.YoutubeRate ?? 0)
            .ThenByDescending(index => Entry(index)?.FullRate ?? 0)
            .ThenBy(index => index)
            .ToList();
    }

    /// <summary>
    /// Порядок показа и перебора: сначала стратегии с лучшим сохранённым качеством,
    /// затем — по истории удачных запусков (YouTube и полный успех).
    /// </summary>
    public static IReadOnlyList<int> GetPriorityOrder(int? preferredIndex = null)
    {
        var historyOrder = GetHistoryOrder();
        var historyPosition = historyOrder
            .Select((index, position) => (index, position))
            .ToDictionary(item => item.index, item => item.position);
        var quality = BypassProfileStore.Ranked("zapret", NetworkIdentity.Key)
            .GroupBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Max(profile => profile.Quality),
                StringComparer.OrdinalIgnoreCase);
        // Стратегия, уже работавшая на этой сети, идёт первой даже при меньшем «качестве».
        var fits = BypassProfileStore.Ranked("zapret", NetworkIdentity.Key)
            .Where(profile => BypassProfileStore.Fits(profile, NetworkIdentity.Key))
            .Select(profile => profile.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var order = Enumerable.Range(0, Presets.Count)
            .OrderByDescending(index => fits.Contains(Presets[index].Name) ? 1 : 0)
            .ThenByDescending(index => quality.TryGetValue(Presets[index].Name, out var value) ? value : 0)
            .ThenBy(index => historyPosition.TryGetValue(index, out var position) ? position : int.MaxValue)
            .ThenBy(index => index)
            .ToList();
        if (preferredIndex is { } preferred && preferred >= 0 && preferred < Presets.Count)
        {
            order.Remove(preferred);
            order.Insert(0, preferred);
        }

        return order;
    }

    private static void SaveHistory(List<ZapretHistoryEntry> history)
    {
        try
        {
            File.WriteAllText(
                AppPaths.ZapretHistoryPath,
                JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("zapret", "Не удалось сохранить историю стратегий", exception);
        }
    }

    private static void RememberResults(List<ZapretHistoryEntry> history, IReadOnlyList<ZapretProbeResult> results)
    {
        foreach (var result in results)
        {
            var entry = history.FirstOrDefault(item => item.Index == result.Index);
            if (entry is null)
            {
                entry = new ZapretHistoryEntry { Index = result.Index, Name = result.Name };
                history.Add(entry);
            }

            entry.Name = result.Name;
            entry.Runs++;
            entry.TotalMs += result.AverageMs;
            entry.LastRunUtc = DateTime.UtcNow;
            if (result.YoutubeOk)
            {
                entry.YoutubeWins++;
            }

            if (result.Ok == result.Total)
            {
                entry.FullWins++;
            }
        }

        SaveHistory(history);
    }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Скачивает недостающие файлы zapret с GitHub (raw, без архивов и антивирусных блокировок zip).</summary>
    public static async Task EnsureAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var file in RequiredFiles)
        {
            var path = Path.Combine(Directory, file);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                continue;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            var rawPath = FileUrls[file];
            progress?.Report("Скачиваю " + file + "…");
            using var response = await Http.GetAsync($"{RepoBase}{rawPath}", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(path);
            await stream.CopyToAsync(target, cancellationToken);
            AppLog.Write("zapret", $"Скачан {file} ({new FileInfo(path).Length} байт)");
        }
    }

    /// <summary>
    /// Аргументы стратегии. gameMode добавляет обход UDP: QUIC, STUN, WireGuard.
    /// hostlistPath ограничивает десинк только перечисленными доменами (используется при автоподборе,
    /// чтобы не трогать посторонний трафик).
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(int presetIndex = 0, bool gameMode = false, string? hostlistPath = null)
    {
        var arguments = Presets[Math.Clamp(presetIndex, 0, Presets.Count - 1)].Arguments.ToList();
        if (!string.IsNullOrEmpty(hostlistPath))
        {
            InjectHostlist(arguments, hostlistPath);
        }

        if (!gameMode)
        {
            return arguments;
        }

        // Игры и голос: QUIC, STUN (Discord/Telegram), WireGuard на типовых портах.
        arguments.Add("--wf-udp=443,3478,19302,50000-50100,51820");
        arguments.AddRange(new[]
        {
            "--filter-l7=quic",
            "--dpi-desync=fake",
            "--dpi-desync-repeats=6",
            "--dpi-desync-fake-quic=files/quic_initial_www_google_com.bin",
            "--new",
            "--filter-l7=stun",
            "--dpi-desync=fake",
            "--dpi-desync-repeats=2",
            "--dpi-desync-fake-stun=files/stun.bin",
            "--new",
            "--filter-l7=wireguard",
            "--dpi-desync=fake",
            "--dpi-desync-repeats=2",
            "--dpi-desync-fake-wireguard=files/wireguard_initiation.bin",
        });
        return arguments;
    }

    private static void InjectHostlist(List<string> arguments, string hostlistPath)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!arguments[index].StartsWith("--filter-tcp=", StringComparison.Ordinal))
            {
                continue;
            }

            // Если у профиля уже есть свой hostlist (официальный пресет YouTube),
            // не подменяем его — иначе профиль начнёт применяться к чужим доменам.
            var end = index + 1;
            var hasOwnHostlist = false;
            while (end < arguments.Count && arguments[end] != "--new")
            {
                if (arguments[end].StartsWith("--hostlist=", StringComparison.Ordinal))
                {
                    hasOwnHostlist = true;
                }

                end++;
            }

            if (!hasOwnHostlist)
            {
                arguments.Insert(index + 1, $"--hostlist={hostlistPath}");
                index++;
            }
        }
    }

    /// <summary>
    /// Список целевых доменов для zapret. Ограничиваем десинк ими, чтобы служебный
    /// трафик — в том числе скрытая проверка серверов — шёл напрямую и не искажался.
    /// </summary>
    public static string EnsureTargetHostlist()
    {
        var path = Path.Combine(AppPaths.RuntimeDirectory, "zapret-target-hosts.txt");
        System.IO.Directory.CreateDirectory(AppPaths.RuntimeDirectory);
        File.WriteAllLines(path, TlsHello.TargetDomains);
        return path;
    }

    public async Task StartAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        int presetIndex = 0,
        bool gameMode = false,
        string? hostlistPath = null,
        bool blockQuic = false)
    {
        if (!IsInstalled)
        {
            throw new InvalidOperationException("zapret не установлен: сначала нажмите «Скачать zapret».");
        }

        Stop();

        // Всегда ограничиваем десинк целевыми доменами: так он не трогает соединения
        // к серверам серверов, и фоновая проверка может работать при включённом zapret.
        var effectiveHostlist = hostlistPath ?? EnsureTargetHostlist();

        if (blockQuic && !SetQuicBlock(true))
        {
            throw new InvalidOperationException(
                "Не удалось включить блокировку QUIC (UDP/443). Проверьте права администратора и службу Windows Firewall.");
        }

        var info = new ProcessStartInfo(ExecutablePath)
        {
            WorkingDirectory = Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in BuildArguments(presetIndex, gameMode, effectiveHostlist))
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            progress?.Report("Запускаю обход DPI (zapret)…");
            _process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить winws.exe");
            SingBoxRunner.KillOnCloseJob.Assign(_process);
            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    AppLog.Write("zapret", e.Data);
                }
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    AppLog.Write("zapret", e.Data);
                }
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Даём ядру время поднять WinDivert и начать захват пакетов.
            await Task.Delay(3000, cancellationToken);
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"zapret завершился с кодом {_process.ExitCode}. " +
                    "Проверьте права администратора и WinDivert (см. журнал).");
            }

            AppLog.Write("zapret", "Обход DPI запущен (winws)");
        }
        catch
        {
            // Не оставляем включённой блокировку QUIC, если обход не поднялся.
            Stop();
            throw;
        }
    }


    public async Task<IReadOnlyList<ZapretProbeResult>> AutoTuneAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        int? preferredIndex = null)
    {
        if (!IsInstalled)
        {
            throw new InvalidOperationException("zapret не установлен.");
        }

        var ytDlpReady = YtDlpVideoProbe.IsInstalled;
        if (!ytDlpReady)
        {
            try
            {
                ytDlpReady = await YtDlpVideoProbe.EnsureAsync(progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                AppLog.Write("zapret", "yt-dlp не подготовлен, проверяю видео только по redirector", exception);
            }
        }

        // Автоподбор гоняет десинк только по целевым доменам: посторонний трафик
        // (банки, рабочие сервисы, сам ассистент) не затрагивается.
        var hostlistPath = Path.Combine(Path.GetTempPath(), "vienlink-zapret-probe-hosts.txt");
        File.WriteAllLines(hostlistPath, new[]
        {
            "youtube.com",
            "googlevideo.com",
            "ytimg.com",
            "t.me",
            "telegram.org",
            "discord.com",
            "discordapp.com",
        });

        try
        {
            using var http = CreateDirectClient(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10));
            var history = LoadHistory().ToList();
            var order = GetPriorityOrder(preferredIndex);
            var probeLimit = ProbeUrls.Length;
            var results = new List<ZapretProbeResult>();
            for (var position = 0; position < order.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = order[position];
                var preset = Presets[index];
                progress?.Report($"Стратегия {position + 1}/{Presets.Count}: «{preset.Name}»…");
                try
                {
                    await StartAsync(null, cancellationToken, index, gameMode: false, hostlistPath: hostlistPath);
                }
                catch (Exception exception)
                {
                    AppLog.Write("zapret", $"Стратегия «{preset.Name}» не запустилась", exception);
                    results.Add(new ZapretProbeResult(index, preset.Name, 0, probeLimit, 0));
                    continue;
                }

                // Больше времени на «прогрев» соединения перед проверками.
                await Task.Delay(2000, cancellationToken);
                var ok = 0;
                var latency = 0;
                var youtubePageOk = false;
                var youtubeServiceOk = false;
                var telegramOk = false;
                var discordOk = false;
                for (var probeIndex = 0; probeIndex < probeLimit; probeIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        using var response = await http.GetAsync(ProbeUrls[probeIndex], HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        if (!IsProbeSuccess(probeIndex, response.StatusCode))
                        {
                            // Если не открылась сама страница YouTube, остальные проверки
                            // для этой стратегии бессмысленны — не тратим время.
                            if (probeIndex == 0)
                            {
                                break;
                            }

                            continue;
                        }

                        ok++;
                        latency += (int)stopwatch.ElapsedMilliseconds;
                        switch (probeIndex)
                        {
                            case 0:
                                youtubePageOk = true;
                                break;
                            case 1:
                                youtubeServiceOk = true;
                                break;
                            case 2:
                                telegramOk = true;
                                break;
                            default:
                                discordOk = true;
                                break;
                        }

                        // Сервер видео открылся — не тратим время на t.me/Discord.
                        if (probeIndex == 1 && youtubeServiceOk)
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (probeIndex == 0)
                        {
                            break;
                        }
                    }
                    catch (HttpRequestException)
                    {
                        if (probeIndex == 0)
                        {
                            break;
                        }
                    }
                }

                // YouTube считается рабочим только если открылась страница и реально пошло
                // видео. redirector.googlevideo.com — быстрый индикатор; если yt-dlp доступен,
                // он подтверждает видео настоящей загрузкой (curl/.NET получают 0 из-за n-sig).
                var youtubeVideoOk = youtubeServiceOk;
                if (youtubePageOk && ytDlpReady)
                {
                    var bytes = await YtDlpVideoProbe.MeasureAsync(cancellationToken);
                    youtubeVideoOk = bytes >= 64 * 1024;
                    if (youtubeVideoOk)
                    {
                        AppLog.Write("zapret", $"Видео YouTube подтверждено yt-dlp: {bytes} байт");
                    }
                }

                var youtubeOk = youtubePageOk && youtubeVideoOk;
                var result = new ZapretProbeResult(
                    index, preset.Name, ok, probeLimit, ok > 0 ? latency / ok : 0,
                    youtubeOk, telegramOk, discordOk, youtubeVideoOk);
                results.Add(result);
                progress?.Report($"  {preset.Name}: {ok}/{probeLimit}, {result.AverageMs} мс");

                Stop();
                await Task.Delay(500, cancellationToken);

                // Досрочно останавливаемся только при полном успехе (все проверочные адреса).
                // Если YouTube работает, а Telegram нет — перебираем дальше и потом выбираем
                // стратегию с рабочим YouTube и лучшей задержкой.
                // Полный автоподбор останавливаем, как только реально пошло видео:
                // именно оно нужно пользователю, а не 204 от страницы.
                if (youtubeOk || ok == probeLimit)
                {
                    progress?.Report(youtubeOk
                        ? $"Видео YouTube пошло — перебор остановлен на «{preset.Name}»"
                        : $"Все проверки прошли — перебор остановлен на «{preset.Name}»");
                    break;
                }
            }

            RememberResults(history, results);
            return results;
        }
        finally
        {
            Stop();
            try
            {
                File.Delete(hostlistPath);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// YouTube строго по generate_204 (страница и сервер видео googlevideo), остальные — любой ответ без ошибки.
    /// </summary>
    private static bool IsProbeSuccess(int probeIndex, System.Net.HttpStatusCode status) =>
        probeIndex <= 1 ? (int)status == 204 : (int)status < 400;

    private static HttpClient CreateDirectClient(
        TimeSpan? timeout = null,
        TimeSpan? connectTimeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(6),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return client;
    }

    /// <summary>Быстрая проверка текущего режима: прямой доступ к YouTube, t.me и Discord.</summary>
    public static async Task<string> ProbeDirectAsync(CancellationToken cancellationToken)
    {
        using var http = CreateDirectClient();
        var parts = new List<string>();
        foreach (var url in ProbeUrls)
        {
            var host = new Uri(url).Host;
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                parts.Add($"{host}: {(int)response.StatusCode}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                parts.Add($"{host}: таймаут");
            }
            catch (HttpRequestException)
            {
                parts.Add($"{host}: нет");
            }
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Включает или снимает системное правило Windows Firewall, блокирующее исходящий
    /// UDP/443. Так видео YouTube переходит с QUIC на TCP, который умеет разрезать zapret.
    /// </summary>
    private bool SetQuicBlock(bool enabled)
    {
        if (enabled)
        {
            // Удаляем возможный остаток от прошлого запуска, иначе netsh откажется
            // создавать правило с тем же именем.
            RunNetsh(quiet: true, "advfirewall", "firewall", "delete", "rule", $"name={QuicBlockRuleName}");
        }

        var ok = enabled
            ? RunNetsh(
                "advfirewall", "firewall", "add", "rule",
                $"name={QuicBlockRuleName}",
                "dir=out",
                "action=block",
                "protocol=UDP",
                "remoteport=443",
                "profile=any",
                "enable=yes")
            : RunNetsh(quiet: true, "advfirewall", "firewall", "delete", "rule", $"name={QuicBlockRuleName}");
        _quicBlocked = enabled && ok;
        if (ok)
        {
            AppLog.Write("zapret", enabled
                ? "QUIC (UDP/443) заблокирован: видео пойдёт по TCP"
                : "Блокировка QUIC (UDP/443) снята");
        }

        return ok;
    }

    /// <summary>Убирает правило, оставшееся после аварийного завершения прошлого запуска.</summary>
    public static void ClearStaleQuicBlock() =>
        RunNetsh(quiet: true, "advfirewall", "firewall", "delete", "rule", $"name={QuicBlockRuleName}");

    private static bool RunNetsh(params string[] arguments) => RunNetsh(quiet: false, arguments);

    private static bool RunNetsh(bool quiet, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo("netsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                return false;
            }

            if (process.ExitCode != 0)
            {
                if (!quiet)
                {
                    AppLog.Write("zapret", $"netsh вернул код {process.ExitCode}: {output.Trim()}");
                }

                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write("zapret", "Не удалось выполнить netsh", exception);
            return false;
        }
    }

    public void Stop()
    {
        if (_quicBlocked)
        {
            SetQuicBlock(false);
        }

        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("zapret", "Не удалось остановить winws", exception);
        }
        finally
        {
            process.Dispose();
            AppLog.Write("zapret", "Обход DPI остановлен");
        }
    }

    public void Dispose() => Stop();
}
