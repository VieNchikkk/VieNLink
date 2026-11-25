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
    public string Key { get; set; } = string.Empty;

    /// <summary>Стратегия страниц (имя <c>StreamFragmentMode</c>).</summary>
    public string Page { get; set; } = string.Empty;

    /// <summary>Стратегия видео-CDN; пусто — как для страниц.</summary>
    public string Video { get; set; } = string.Empty;

    /// <summary>Блокировать ли QUIC (UDP/443) на этой сети.</summary>
    public bool BlockQuic { get; set; }

    /// <summary>Как реально шло видео: пусто — неизвестно, tcp — через обход, quic — релей.</summary>
    public string VideoTransport { get; set; } = string.Empty;

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

    public List<ProviderPreset> Presets { get; set; } = new();
}

/// <summary>
/// База параметров обхода по сетям. Локальная (собранная на устройстве) главнее зашитой;
/// зашитая лежит ресурсом в <c>VieNLink.Core</c> под именем <c>provider-presets.json</c>
/// и обновляется в следующем релизе после сбора экспорта.
/// </summary>
internal static class ProviderPresetStore
{
    private const string ShippedFileName = "provider-presets.json";

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static ProviderPresetFile? _shipped;
    private static ProviderPresetFile? _localCache;
    private static DateTime _localStamp;
    private static long _localLength = -1;
    private static string? _pathOverride;

    /// <summary>Путь к локальной базе (в self-test подменяется временным файлом).</summary>
    internal static string? PathOverride
    {
        get => _pathOverride;
        set
        {
            _pathOverride = value;
            _shipped = null;
            _localCache = null;
            _localStamp = DateTime.MinValue;
            _localLength = -1;
        }
    }

    public static string LocalPath => _pathOverride ?? AppPaths.ProviderPresetsPath;

    /// <summary>Параметры для сети: сначала локальные, затем зашитые в сборку.</summary>
    public static ProviderPreset? Find(string key) => Find(key, out _);

    /// <summary>То же, но сообщает, откуда запись: из зашитой базы или собранная на устройстве.</summary>
    public static ProviderPreset? Find(string key, out bool fromShipped)
    {
        fromShipped = false;
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        lock (Gate)
        {
            var local = LoadLocal();
            var preset = Match(local, key);
            if (preset is not null)
            {
                return preset;
            }

            fromShipped = true;
            return Match(LoadShipped(), key);
        }
    }

    /// <summary>
    /// Пользователь выбрал стратегию вручную: запоминаем её как ручную. Автоподбор
    /// такие записи не перезаписывает, только увеличивает счётчик подтверждений.
    /// </summary>
    public static void RecordChoice(string key, string page, string video, bool blockQuic)
    {
        Upsert(key, page, video, blockQuic, manual: true, success: false, string.Empty, 0);
    }

    /// <summary>Стратегия реально открыла страницу и превью (или поток видео): подтверждаем.</summary>
    public static void RecordSuccess(string key, string page, string video, bool blockQuic,
        string transport = "", int videoKbps = 0)
    {
        Upsert(key, page, video, blockQuic, manual: false, success: true, transport, videoKbps);
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
        string transport, int videoKbps)
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

            // Ручной выбор не затираем автоподбором: он и есть предпочтение пользователя.
            if (manual || !string.Equals(preset.Source, "manual", StringComparison.Ordinal))
            {
                preset.Page = page;
                preset.Video = video;
                preset.BlockQuic = blockQuic;
                preset.Source = manual ? "manual" : "auto";
            }

            if (success)
            {
                preset.Successes++;
                preset.LastSuccessUtc = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(transport))
                {
                    preset.VideoTransport = transport;
                }

                if (videoKbps > 0)
                {
                    preset.VideoKbps = videoKbps;
                }
            }

            file.UpdatedUtc = DateTime.UtcNow;
            JsonFileStore.Write(LocalPath, file);
            _localCache = null; // перечитаем с диска при следующем запросе
            AppLog.Write("presets",
                $"Параметры обхода {key}: страница {preset.Page}, " +
                $"видео {(string.IsNullOrWhiteSpace(preset.Video) ? "как для страниц" : preset.Video)}, " +
                $"QUIC {(preset.BlockQuic ? "блокируется" : "через релей")}, " +
                $"поток {(string.IsNullOrWhiteSpace(preset.VideoTransport) ? "не подтверждён" : preset.VideoTransport)}, " +
                $"{preset.Source}, подтверждений {preset.Successes}");
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

            var loaded = JsonFileStore.Read<ProviderPresetFile>(LocalPath);
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

    private static ProviderPreset? Match(ProviderPresetFile file, string key) =>
        file.Presets.FirstOrDefault(preset =>
            preset.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}
