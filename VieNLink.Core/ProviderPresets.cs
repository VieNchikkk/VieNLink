using System.IO;
using System.Text.Json;

namespace VieNLink;

/// <summary>
/// Подтверждённые параметры TUN-обхода для одной сети — обычно мобильного оператора
/// (ключ «cell:&lt;MCCMNC&gt;:&lt;оператор&gt;»). Собираются на устройстве при удачном подборе,
/// экспортируются разработчику и попадают в зашитую базу как стартовые для новых установок.
/// Wi-Fi SSID в базу и экспорт не попадают: это личные данные.
/// </summary>
internal sealed class ProviderPreset
{
    /// <summary>Ключ сети: «cell:&lt;MCCMNC&gt;» (имя оператора хранится отдельно).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Имя оператора на момент записи — только для людей и экспорта, ключом не является.</summary>
    public string Operator { get; set; } = string.Empty;

    /// <summary>Стратегия страниц (имя <c>StreamFragmentMode</c>).</summary>
    public string Page { get; set; } = string.Empty;

    /// <summary>Стратегия видео-CDN; пусто — как для страниц.</summary>
    public string Video { get; set; } = string.Empty;

    /// <summary>Блокировать ли QUIC (UDP/443) на этой сети.</summary>
    public bool BlockQuic { get; set; }

    /// <summary>Как реально шло видео: пусто — неизвестно, tcp — через обход, quic — релей.</summary>
    public string VideoTransport { get; set; } = string.Empty;

    /// <summary>Когда видео последний раз реально шло (по TCP или по QUIC через релей).</summary>
    public DateTime VideoSeenUtc { get; set; }

    /// <summary>Скорость видео при последнем подтверждении, КБ/с (0 — не измерялась).</summary>
    public int VideoKbps { get; set; }

    /// <summary>auto — подтверждено подбором, manual — выбрано пользователем.</summary>
    public string Source { get; set; } = "auto";

    public int Successes { get; set; }

    public DateTime LastSuccessUtc { get; set; }
}

internal sealed class ProviderPresetFile
{
    public int Version { get; set; } = 1;

    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// Адрес приёмника подборок (Cloudflare Worker / Yandex Cloud Function). Живёт в базе,
    /// а не в коде: когда приёмник поднят, URL доезжает до установок вместе с базой —
    /// новая версия приложения для этого не нужна. Пусто — отправка выключена.
    /// </summary>
    public string UploadUrl { get; set; } = string.Empty;

    public List<ProviderPreset> Presets { get; set; } = new();
}

/// <summary>Откуда взяты параметры сети: своё устройство, обновляемая база или зашитая сборка.</summary>
internal enum PresetOrigin
{
    /// <summary>Собрано и подтверждено на этом устройстве.</summary>
    Device,

    /// <summary>Из базы, обновляемой из репозитория (приходит без новой версии).</summary>
    Remote,

    /// <summary>Зашито в сборку на момент выпуска.</summary>
    Shipped,
}

/// <summary>Состояние проверки обновляемой базы: когда проверяли, когда получилось, сколько записей.</summary>
internal sealed class ProviderPresetSync
{
    public DateTime LastCheckUtc { get; set; }

    public DateTime LastOkUtc { get; set; }

    public int Count { get; set; }
}

/// <summary>
/// База параметров обхода по сетям. Локальная (собранная на устройстве) главнее зашитой;
/// зашитая лежит ресурсом в <c>VieNLink.Core</c> под именем <c>provider-presets.json</c>
/// и обновляется в следующем релизе после сбора экспорта.
/// </summary>
internal static class ProviderPresetStore
{
    private const string ShippedFileName = "provider-presets.json";

    /// <summary>
    /// Обновляемая база: тот же файл, что зашит в сборку, но читается из репозитория —
    /// новые параметры доезжают до всех установок без выпуска версии. Зеркала GitHub
    /// (ghfast/gh-proxy) подставляет загрузчик источников.
    /// </summary>
    public const string RemoteUrl =
        "https://raw.githubusercontent.com/VieNchikkk/VieNLink/main/VieNLink.Core/Assets/provider-presets.json";

    /// <summary>В сеть за базой — не чаще раза в сутки: файл меняется редко.</summary>
    private static readonly TimeSpan RemoteInterval = TimeSpan.FromHours(24);

    /// <summary>Предел доверия к удалённому файлу: больше записей он не должен содержать.</summary>
    private const int MaxRemotePresets = 2000;

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Файл базы ведётся вручную и лежит в репозитории строчными именами: читаем оба
        // написания, иначе зашитая/обновляемая база молча оказывалась пустой.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static ProviderPresetFile? _shipped;
    private static ProviderPresetFile? _localCache;
    private static DateTime _localStamp;
    private static long _localLength = -1;
    private static ProviderPresetFile? _remoteCache;
    private static DateTime _remoteStamp;
    private static long _remoteLength = -1;
    private static string? _pathOverride;

    /// <summary>Путь к локальной базе (в self-test подменяется временным файлом).</summary>
    internal static string? PathOverride
    {
        get => _pathOverride;
        set
        {
            _pathOverride = value;
            _shipped = null;
            ResetCaches();
        }
    }

    public static string LocalPath => _pathOverride ?? AppPaths.ProviderPresetsPath;

    /// <summary>Обновляемая база на устройстве (кэш скачанного файла).</summary>
    internal static string RemotePath => _pathOverride is null
        ? AppPaths.ProviderPresetsRemotePath
        : Path.ChangeExtension(_pathOverride, ".remote.json");

    /// <summary>Состояние проверки обновляемой базы.</summary>
    internal static string SyncPath => _pathOverride is null
        ? AppPaths.ProviderPresetsSyncPath
        : Path.ChangeExtension(_pathOverride, ".sync.json");

    /// <summary>
    /// Параметры для сети: сначала собранные на этом устройстве, затем обновляемая база
    /// из репозитория, затем зашитая в сборку.
    /// </summary>
    public static ProviderPreset? Find(string key) => Find(key, out _);

    /// <summary>То же, но сообщает, откуда запись — это показывается пользователю.</summary>
    public static ProviderPreset? Find(string key, out PresetOrigin origin)
    {
        origin = PresetOrigin.Device;
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        lock (Gate)
        {
            var preset = Match(LoadLocal(), key);
            if (preset is not null)
            {
                return preset;
            }

            preset = Match(LoadRemote(), key);
            if (preset is not null)
            {
                origin = PresetOrigin.Remote;
                return preset;
            }

            origin = PresetOrigin.Shipped;
            return Match(LoadShipped(), key);
        }
    }

    /// <summary>
    /// Подтянуть обновляемую базу из репозитория. Не чаще раза в сутки (или принудительно),
    /// тихо: при недоступной сети остаётся прежний кэш. Возвращает true, если база изменилась.
    /// </summary>
    public static async Task<bool> RefreshRemoteAsync(CancellationToken cancellationToken,
        bool force = false)
    {
        var sync = SyncStatus();
        if (!force && sync.LastCheckUtc != default &&
            DateTime.UtcNow - sync.LastCheckUtc < RemoteInterval)
        {
            return false;
        }

        // Отметку ставим до запроса: недоступный GitHub не должен дёргаться на каждом такте.
        sync.LastCheckUtc = DateTime.UtcNow;
        SaveSync(sync);

        var source = new SourceDefinition
        {
            Id = "provider-presets",
            Name = "База параметров обхода",
            Url = RemoteUrl,
        };

        string text;
        try
        {
            (text, _) = await NodeSourceService
                .FetchRawAsync(source, cancellationToken, TimeSpan.FromSeconds(12))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Write("presets", "Обновляемая база параметров обхода недоступна", exception);
            return false;
        }

        if (!TryParseRemote(text, out var file, out var error))
        {
            AppLog.Write("presets", $"Обновляемая база параметров обхода отклонена: {error}");
            return false;
        }

        var changed = false;
        lock (Gate)
        {
            changed = !SameContent(file, LoadRemote());
            if (changed)
            {
                try
                {
                    JsonFileStore.Write(RemotePath, file, Options);
                    ResetCaches();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    AppLog.Write("presets", "Не удалось сохранить обновляемую базу параметров обхода", exception);
                    return false;
                }
            }
        }

        sync.LastOkUtc = DateTime.UtcNow;
        sync.Count = file.Presets.Count;
        SaveSync(sync);
        AppLog.Write("presets",
            $"База операторов из репозитория: {file.Presets.Count} записей" +
            (changed ? " (обновлена)" : " (без изменений)"));
        return changed;
    }

    /// <summary>
    /// Адрес приёмника подборок: сначала из обновляемой базы, затем из зашитой.
    /// Пусто — приёмник ещё не поднят, отправка остаётся выключенной.
    /// </summary>
    public static string UploadUrl
    {
        get
        {
            lock (Gate)
            {
                var remote = LoadRemote().UploadUrl;
                if (!string.IsNullOrWhiteSpace(remote))
                {
                    return remote.Trim();
                }

                return (LoadShipped().UploadUrl ?? string.Empty).Trim();
            }
        }
    }

    /// <summary>Состояние последней проверки обновляемой базы (для экрана и самопроверки).</summary>
    internal static ProviderPresetSync SyncStatus()    {
        try
        {
            return JsonFileStore.Read<ProviderPresetSync>(SyncPath) ?? new ProviderPresetSync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ProviderPresetSync();
        }
    }

    /// <summary>
    /// Разбор удалённого файла базы: принимаем только мобильных операторов и известные
    /// стратегии. Wi-Fi SSID — личные данные, в общей базе их быть не должно.
    /// </summary>
    internal static bool TryParseRemote(string text, out ProviderPresetFile file, out string error)
    {
        file = new ProviderPresetFile();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "пустой ответ";
            return false;
        }

        ProviderPresetFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ProviderPresetFile>(text, Options);
        }
        catch (JsonException exception)
        {
            error = "файл не разбирается: " + exception.Message;
            return false;
        }

        if (parsed?.Presets is null)
        {
            error = "нет списка параметров";
            return false;
        }

        if (parsed.Presets.Count > MaxRemotePresets)
        {
            error = $"слишком много записей: {parsed.Presets.Count}";
            return false;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in parsed.Presets)
        {
            if (preset is null ||
                !preset.Key.StartsWith("cell:", StringComparison.OrdinalIgnoreCase) ||
                !IsKnownStrategy(preset.Page) ||
                (!string.IsNullOrWhiteSpace(preset.Video) && !IsKnownStrategy(preset.Video)) ||
                !keys.Add(preset.Key))
            {
                continue;
            }

            file.Presets.Add(preset);
        }

        file.Version = Math.Max(1, parsed.Version);
        file.UpdatedUtc = parsed.UpdatedUtc;
        file.UploadUrl = IsHttpUrl(parsed.UploadUrl) ? parsed.UploadUrl.Trim() : string.Empty;
        return true;
    }

    /// <summary>Адрес приёмника принимаем только http(s): иначе в базу можно вписать что угодно.</summary>
    private static bool IsHttpUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static bool IsKnownStrategy(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Enum.GetNames<StreamFragmentMode>().Contains(value, StringComparer.OrdinalIgnoreCase);

    private static bool SameContent(ProviderPresetFile left, ProviderPresetFile right) =>
        string.Equals(left.UploadUrl ?? string.Empty, right.UploadUrl ?? string.Empty,
            StringComparison.Ordinal) &&
        left.Presets.Count == right.Presets.Count &&
        left.Presets.Zip(right.Presets).All(pair =>
            pair.First.Key.Equals(pair.Second.Key, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pair.First.Page, pair.Second.Page, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pair.First.Video ?? string.Empty, pair.Second.Video ?? string.Empty,
                StringComparison.OrdinalIgnoreCase) &&
            pair.First.BlockQuic == pair.Second.BlockQuic);

    private static void ResetCaches()
    {
        _shipped = null;
        _localCache = null;
        _localStamp = DateTime.MinValue;
        _localLength = -1;
        _remoteCache = null;
        _remoteStamp = DateTime.MinValue;
        _remoteLength = -1;
    }

    private static void SaveSync(ProviderPresetSync sync)
    {
        try
        {
            JsonFileStore.Write(SyncPath, sync);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("presets", "Не удалось сохранить состояние обновления базы", exception);
        }
    }

    /// <summary>Кэш скачанной базы: читается с диска только при изменении файла.</summary>
    private static ProviderPresetFile LoadRemote()
    {
        try
        {
            var info = new FileInfo(RemotePath);
            if (_remoteCache is not null && info.Exists &&
                info.LastWriteTimeUtc == _remoteStamp && info.Length == _remoteLength)
            {
                return _remoteCache;
            }

            var loaded = JsonFileStore.Read<ProviderPresetFile>(RemotePath, Options);
            if (loaded is null)
            {
                return _remoteCache = new ProviderPresetFile();
            }

            loaded.Presets ??= new List<ProviderPreset>();
            _remoteCache = loaded;
            _remoteStamp = info.LastWriteTimeUtc;
            _remoteLength = info.Length;
            return loaded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("presets", "Не удалось прочитать обновляемую базу параметров обхода", exception);
            return new ProviderPresetFile();
        }
    }

    /// <summary>
    /// Пользователь выбрал стратегию вручную: запоминаем её как ручную. Автоподбор
    /// такие записи не перезаписывает, только увеличивает счётчик подтверждений.
    /// </summary>
    public static void RecordChoice(string key, string page, string video, bool blockQuic,
        string operatorName = "")
    {
        Upsert(key, page, video, blockQuic, manual: true, success: false, string.Empty, 0, operatorName);
    }

    /// <summary>Стратегия реально открыла страницу и превью (или поток видео): подтверждаем.</summary>
    public static void RecordSuccess(string key, string page, string video, bool blockQuic,
        string transport = "", int videoKbps = 0, string operatorName = "")
    {
        Upsert(key, page, video, blockQuic, manual: false, success: true, transport, videoKbps,
            operatorName);
    }

    /// <summary>
    /// Реальный видеопоток через обход (по TCP или по QUIC через релей): записываем, как шло
    /// видео, но подтверждений не накручиваем — доказательством TCP-стратегии это не является.
    /// </summary>
    public static void RecordVideo(string key, string page, string video, bool blockQuic,
        string transport, string operatorName = "")
    {
        Upsert(key, page, video, blockQuic, manual: false, success: false, transport, 0, operatorName);
    }

    /// <summary>Сколько записей попадёт в экспорт (только мобильные операторы).</summary>
    public static int ExportCount()
    {
        lock (Gate)
        {
            return LoadLocal().Presets.Count(preset =>
                !string.IsNullOrWhiteSpace(preset.Key) &&
                preset.Key.StartsWith("cell", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Экспорт для сбора статистики: по умолчанию только мобильные операторы.</summary>
    public static string ExportJson(bool includeWifi = false)
    {
        lock (Gate)
        {
            var local = LoadLocal();
            var file = new ProviderPresetFile
            {
                UpdatedUtc = DateTime.UtcNow,
                Presets = local.Presets
                    .Where(preset => !string.IsNullOrWhiteSpace(preset.Key))
                    .Where(preset => includeWifi ||
                                     preset.Key.StartsWith("cell", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(preset => preset.Key, StringComparer.Ordinal)
                    .ToList(),
            };
            return JsonSerializer.Serialize(file, Options);
        }
    }

    /// <summary>Зашитая база (ресурс сборки); пустая, если файла ещё нет.</summary>
    public static ProviderPresetFile LoadShipped()
    {
        lock (Gate)
        {
            if (_shipped is not null)
            {
                return _shipped;
            }

            _shipped = new ProviderPresetFile();
            try
            {
                var assembly = typeof(ProviderPresetStore).Assembly;
                var name = Array.Find(assembly.GetManifestResourceNames(),
                    item => item.EndsWith(ShippedFileName, StringComparison.Ordinal));
                if (name is null)
                {
                    return _shipped;
                }

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null)
                {
                    return _shipped;
                }

                var loaded = JsonSerializer.Deserialize<ProviderPresetFile>(stream, Options);
                if (loaded is not null)
                {
                    loaded.Presets ??= new List<ProviderPreset>();
                    _shipped = loaded;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException
                                                  or NotSupportedException)
            {
                AppLog.Write("presets", "Не удалось прочитать зашитую базу параметров обходов", exception);
            }

            return _shipped;
        }
    }

    private static void Upsert(string key, string page, string video, bool blockQuic, bool manual, bool success,
        string transport, int videoKbps, string operatorName = "")
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            // Сеть не определена — не пишем в чужой пресет.
            return;
        }

        lock (Gate)
        {
            var file = LoadLocal();
            var preset = Match(file, key);
            if (preset is null)
            {
                preset = new ProviderPreset { Key = key };
                file.Presets.Add(preset);
            }
            else if (!preset.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                // Запись из старой версии с именем оператора в ключе — переносим на MCCMNC.
                AppLog.Write("presets", $"Ключ записи {preset.Key} приведён к {key}");
                preset.Key = key;
            }

            if (!string.IsNullOrWhiteSpace(operatorName))
            {
                preset.Operator = operatorName;
            }

            // Ручной выбор не затираем автоподбором: он и есть предпочтение пользователя.
            if (manual || !string.Equals(preset.Source, "manual", StringComparison.Ordinal))
            {
                preset.Page = page;
                preset.Video = video;
                preset.BlockQuic = blockQuic;
                preset.Source = manual ? "manual" : "auto";
            }

            // Как реально шло видео: отмечаем и без засчитанного подтверждения, иначе
            // «поток не подтверждён» висит, хотя видео смотрели (по QUIC через релей).
            if (!string.IsNullOrWhiteSpace(transport))
            {
                preset.VideoTransport = transport;
                preset.VideoSeenUtc = DateTime.UtcNow;
            }

            if (success)
            {
                preset.Successes++;
                preset.LastSuccessUtc = DateTime.UtcNow;
                if (videoKbps > 0)
                {
                    preset.VideoKbps = videoKbps;
                }
            }

            file.UpdatedUtc = DateTime.UtcNow;
            JsonFileStore.Write(LocalPath, file, Options);
            _localCache = null; // перечитаем с диска при следующем запросе
            AppLog.Write("presets",
                $"Параметры обхода {key}: страница {preset.Page}, " +
                $"видео {(string.IsNullOrWhiteSpace(preset.Video) ? "как для страниц" : preset.Video)}, " +
                $"QUIC {(preset.BlockQuic ? "блокируется" : "через релей")}, " +
                $"поток {(string.IsNullOrWhiteSpace(preset.VideoTransport) ? "не подтверждён" : preset.VideoTransport)}, " +
                $"{preset.Source}, подтверждений {preset.Successes}");

            // Пользователь разрешил делиться подборками — ставим в очередь отправки.
            // Без согласия вызов ничего не делает и в очередь не попадает.
            ProviderPresetShare.Enqueue(preset);
        }
    }

    private static ProviderPresetFile LoadLocal()
    {
        try
        {
            // Экран обхода читает пресеты каждые 2 с: файл разбираем только при изменении.
            var info = new FileInfo(LocalPath);
            if (_localCache is not null && info.Exists &&
                info.LastWriteTimeUtc == _localStamp && info.Length == _localLength)
            {
                return _localCache;
            }

            var loaded = JsonFileStore.Read<ProviderPresetFile>(LocalPath, Options);
            if (loaded is null)
            {
                return _localCache = new ProviderPresetFile();
            }

            loaded.Presets ??= new List<ProviderPreset>();
            _localCache = loaded;
            _localStamp = info.LastWriteTimeUtc;
            _localLength = info.Length;
            return loaded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("presets", "Не удалось прочитать базу параметров обходов", exception);
            return new ProviderPresetFile();
        }
    }

    private static ProviderPreset? Match(ProviderPresetFile file, string key)
    {
        var exact = file.Presets.FirstOrDefault(preset =>
            preset.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (exact is not null || !key.StartsWith("cell:", StringComparison.OrdinalIgnoreCase))
        {
            return exact;
        }

        // Записи прежних версий и других устройств содержали имя оператора в ключе
        // («cell:25002:МегаФон»): для сети по MCCMNC такая запись тоже годится.
        return file.Presets.FirstOrDefault(preset =>
            preset.Key.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
    }
}
