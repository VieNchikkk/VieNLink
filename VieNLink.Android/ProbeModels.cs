using System.Text.Json;

namespace VieNLink.Mobile;

/// <summary>Запрос на проверку серверов отдельным процессом :probe.</summary>
internal sealed class ProbeRequest
{
    public List<ProbeRequestNode> Nodes { get; set; } = new();

    /// <summary>
    /// false — быстрая перепроверка: базовые адреса сервисов без обхода
    /// 18 заблокированных доменов (для пятиминутного освежения счётчиков).
    /// </summary>
    public bool FullDomains { get; set; } = true;
}

internal sealed class ProbeRequestNode
{
    public string Id { get; set; } = string.Empty;
    public string RawLink { get; set; } = string.Empty;

    /// <summary>
    /// Прежний статус сервера: процесс :probe разбирает ссылку в новый объект, и без этого
    /// он считал бы любой сервер непроверенным, удаляя рабочий после первого же провала.
    /// </summary>
    public NodeStatus Status { get; set; }

    /// <summary>Счётчик провалов подряд: рабочий сервер удаляется после 2-го.</summary>
    public int FailCount { get; set; }

    /// <summary>
    /// Результат последней удачной проверки: его тоже пересылаем в :probe, иначе при
    /// мягком провале рабочего сервера сохранять было бы нечего — :probe разбирает ссылку
    /// заново, и флаги оказались бы пустыми, а основной процесс затирал бы реальные отметки.
    /// </summary>
    public int? DelayMs { get; set; }
    public DateTime? CheckedAtUtc { get; set; }
    public bool? YoutubeOk { get; set; }
    public bool? TelegramOk { get; set; }
    public bool? UniversalOk { get; set; }
    public bool? DiscordOk { get; set; }
    public int OpenDomains { get; set; }
}

/// <summary>Результаты проверки: основной процесс применяет их к своему NodeStore.</summary>
internal sealed class ProbeResultFile
{
    public List<ProbeResultNode> Nodes { get; set; } = new();
    public string? Error { get; set; }
}

internal sealed class ProbeResultNode
{
    public string Id { get; set; } = string.Empty;
    public NodeStatus Status { get; set; }
    public int? DelayMs { get; set; }
    public DateTime? CheckedAtUtc { get; set; }

    /// <summary>Когда была последняя попытка проверки (даже если партия откатилась).</summary>
    public DateTime? TriedAtUtc { get; set; }

    public bool? YoutubeOk { get; set; }
    public bool? TelegramOk { get; set; }
    public bool? UniversalOk { get; set; }
    public bool? DiscordOk { get; set; }
    public int OpenDomains { get; set; }
    public int FailCount { get; set; }
}

/// <summary>
/// Ход проверки: процесс :probe пишет его в файл, основной процесс показывает в интерфейсе.
/// Так пользователь видит, что проверка идёт, сколько серверов уже пройдено и сколько рабочих.
/// </summary>
internal sealed class ProbeProgressFile
{
    public string Stage { get; set; } = string.Empty;
    public int Tested { get; set; }
    public int Total { get; set; }
    public int Working { get; set; }
}

/// <summary>Ход проверки для интерфейса: стадия, пройдено, всего, рабочих.</summary>
internal sealed record ProbeProgress(string Stage, int Tested, int Total, int Working)
{
    public string Describe() => Total > 0
        ? $"{Stage}: {Tested} из {Total} · рабочих {Working}"
        : Stage;
}

internal static class ProbeProtocol
{
    public const string ExtraRequest = "probeRequest";

    /// <summary>Команда основного процесса: остановить текущую проверку и погасить :probe.</summary>
    public const string StopAction = "com.vienlink.app.STOP_PROBE";

    public static string RequestPath => Path.Combine(AppPaths.DataDirectory, "probe-request.json");

    public static string ResultPath => Path.Combine(AppPaths.DataDirectory, "probe-results.json");

    /// <summary>Файл хода проверки: обновляется процессом :probe во время работы.</summary>
    public static string ProgressPath => Path.Combine(AppPaths.DataDirectory, "probe-progress.json");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null,
    };
}
