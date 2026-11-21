using System.Text.Json;

namespace VieNLink.Mobile;

/// <summary>Запрос на проверку узлов отдельным процессом :probe.</summary>
internal sealed class ProbeRequest
{
    public List<ProbeRequestNode> Nodes { get; set; } = new();
}

internal sealed class ProbeRequestNode
{
    public string Id { get; set; } = string.Empty;
    public string RawLink { get; set; } = string.Empty;
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
    public bool? YoutubeOk { get; set; }
    public bool? TelegramOk { get; set; }
    public bool? UniversalOk { get; set; }
    public bool? DiscordOk { get; set; }
    public int OpenDomains { get; set; }
    public int FailCount { get; set; }
}

internal static class ProbeProtocol
{
    public const string ExtraRequest = "probeRequest";

    public static string RequestPath => Path.Combine(AppPaths.DataDirectory, "probe-request.json");

    public static string ResultPath => Path.Combine(AppPaths.DataDirectory, "probe-results.json");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null,
    };
}
