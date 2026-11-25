using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace VieNLink;

/// <summary>
/// Одна запись для приёмника: сеть и стратегии обхода. Ничего личного — ни SSID,
/// ни адресов, ни идентификатора устройства.
/// </summary>
internal sealed class SharedPreset
{
    public int Version { get; set; } = 1;

    /// <summary>Ключ сети, только «cell:&lt;MCCMNC&gt;:&lt;оператор&gt;».</summary>
    public string Key { get; set; } = string.Empty;

    public string Page { get; set; } = string.Empty;

    public string Video { get; set; } = string.Empty;

    public bool BlockQuic { get; set; }

    public string VideoTransport { get; set; } = string.Empty;

    public int VideoKbps { get; set; }

    /// <summary>Сколько раз параметры подтвердились на этом устройстве.</summary>
    public int Successes { get; set; }

    /// <summary>Версия приложения: помогает понять, каким кодом подобрано.</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>Когда запись сделана. Не идентификатор устройства, а метка одной подборки.</summary>
    public DateTime RecordedUtc { get; set; }
}

/// <summary>
/// Отправка подтверждённых параметров обхода разработчику. Работает только при двух
/// условиях: пользователь поставил галочку согласия (<see cref="Enabled"/>) и поднят
/// приёмник (<see cref="Endpoint"/>). Пока приёмник отложен, очередь пустая и в сеть
/// ничего не уходит. Неудачные отправки ждут в очереди до следующего обслуживания.
/// </summary>
internal static class ProviderPresetShare
{
    /// <summary>
    /// Адрес приёмника (Cloudflare Worker / Yandex Cloud Function). Пусто — приём отложен:
    /// галочка в настройках не показывается, отправка не включается. Когда приёмник
    /// поднят, достаточно вписать URL сюда — остальной путь уже готов.
    /// </summary>
    public const string Endpoint = "";

    /// <summary>Сколько записей держим в очереди: старые вытесняются новыми.</summary>
    private const int MaxQueue = 100;

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>Согласие пользователя: выставляется из настроек при их загрузке и по галочке.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Приёмник настроен — только тогда галочка согласия имеет смысл.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);

    /// <summary>Сколько записей ждёт отправки (для настроек и самопроверки).</summary>
    public static int QueueCount
    {
        get
        {
            lock (Gate)
            {
                return LoadQueue().Count;
            }
        }
    }

    /// <summary>
    /// Поставить подтверждённые параметры в очередь. Без согласия и без приёмника —
    /// ничего не делает: данные не покидают устройство и не копятся.
    /// </summary>
    public static void Enqueue(ProviderPreset preset)
    {
        if (!Enabled || !IsConfigured ||
            string.IsNullOrWhiteSpace(preset.Key) ||
            !preset.Key.StartsWith("cell:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (Gate)
        {
            var queue = LoadQueue();
            var shared = ToShared(preset);
            var existing = queue.FindIndex(item => SameChoice(item, shared));
            if (existing >= 0)
            {
                queue[existing] = shared;
            }
            else
            {
                queue.Add(shared);
            }

            while (queue.Count > MaxQueue)
            {
                queue.RemoveAt(0);
            }

            SaveQueue(queue);
            AppLog.Write("presets",
                $"Параметры {preset.Key} в очереди на отправку (ждут {queue.Count})");
        }
    }

    /// <summary>Отправить очередь. Возвращает число отправленных записей.</summary>
    public static async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || !IsConfigured)
        {
            return 0;
        }

        List<SharedPreset> queue;
        lock (Gate)
        {
            queue = LoadQueue();
        }

        var sent = 0;
        foreach (var item in queue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await SendOnceAsync(item, cancellationToken).ConfigureAwait(false))
            {
                // Сеть пропала или приёмник сбоит: остальное подождёт следующего раза.
                break;
            }

            sent++;
            lock (Gate)
            {
                var rest = LoadQueue();
                rest.RemoveAll(kept => SameRecord(kept, item));
                SaveQueue(rest);
            }
        }

        if (sent > 0)
        {
            AppLog.Write("presets", $"Отправлено параметров обхода: {sent}");
        }

        return sent;
    }

    /// <summary>Очередь как есть (для настроек и самопроверки).</summary>
    internal static IReadOnlyList<SharedPreset> Peek()
    {
        lock (Gate)
        {
            return LoadQueue();
        }
    }

    private static async Task<bool> SendOnceAsync(SharedPreset item, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(item, Options);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(Endpoint, content, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                // Приёмник отказал самой записи: повторять её бессмысленно, убираем из очереди.
                AppLog.Write("presets",
                    $"Приёмник отклонил параметры ({item.Key}, HTTP {(int)response.StatusCode}) — убрано из очереди");
                return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException ||
                                          (exception is TaskCanceledException &&
                                           !cancellationToken.IsCancellationRequested))
        {
            AppLog.Write("presets", "Отправка параметров обхода не удалась", exception);
            return false;
        }
    }

    private static SharedPreset ToShared(ProviderPreset preset) => new()
    {
        Key = preset.Key,
        Page = preset.Page,
        Video = preset.Video ?? string.Empty,
        BlockQuic = preset.BlockQuic,
        VideoTransport = preset.VideoTransport ?? string.Empty,
        VideoKbps = preset.VideoKbps,
        Successes = preset.Successes,
        AppVersion = AppInfo.Version,
        RecordedUtc = preset.LastSuccessUtc == default ? DateTime.UtcNow : preset.LastSuccessUtc,
    };

    private static string QueuePath => AppPaths.ProviderPresetsOutboxPath;

    private static List<SharedPreset> LoadQueue()
    {
        try
        {
            return JsonFileStore.Read<List<SharedPreset>>(QueuePath) ?? new List<SharedPreset>();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("presets", "Не удалось прочитать очередь параметров обхода", exception);
            return new List<SharedPreset>();
        }
    }

    private static void SaveQueue(List<SharedPreset> queue)
    {
        try
        {
            JsonFileStore.Write(QueuePath, queue);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("presets", "Не удалось сохранить очередь параметров обхода", exception);
        }
    }

    /// <summary>Та же сеть и те же стратегии — запись в очереди заменяем, а не дублируем.</summary>
    private static bool SameChoice(SharedPreset left, SharedPreset right) =>
        left.Key.Equals(right.Key, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Page, right.Page, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Video ?? string.Empty, right.Video ?? string.Empty,
            StringComparison.OrdinalIgnoreCase) &&
        left.BlockQuic == right.BlockQuic;

    /// <summary>Та же запись (для удаления из перечитанной с диска очереди).</summary>
    private static bool SameRecord(SharedPreset left, SharedPreset right) =>
        SameChoice(left, right) && left.RecordedUtc == right.RecordedUtc;
}
