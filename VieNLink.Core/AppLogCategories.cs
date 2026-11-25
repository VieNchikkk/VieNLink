namespace VieNLink;

/// <summary>
/// Категории журнала. В каждой строке уже есть тег (`tun-bypass`, `probe`, `mtproto`…),
/// поэтому отдельные файлы не нужны: фильтр по тегу даёт те же вкладки, а суточный файл
/// остаётся один (с ротацией по 2 МБ). «Ошибки» — сквозная категория: строка с исключением
/// остаётся видна и в своей категории (например, «Обход»), и в «Ошибках».
/// </summary>
internal static class AppLogCategories
{
    public const string All = "Всё";
    public const string Bypass = "Обход";
    public const string Probes = "Проверки";
    public const string MtProto = "MTProto";
    public const string Updates = "Обновления";
    public const string Errors = "Ошибки";

    /// <summary>Порядок вкладок в интерфейсе.</summary>
    public static IReadOnlyList<string> Names { get; } = new[] { All, Bypass, Probes, MtProto, Updates, Errors };

    /// <summary>Тег строки журнала: `ЧЧ:мм:сс.мс  тег  текст`.</summary>
    public static string TagOf(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        var first = line.IndexOf("  ", StringComparison.Ordinal);
        if (first < 0)
        {
            return string.Empty;
        }

        var start = first + 2;
        var second = line.IndexOf("  ", start, StringComparison.Ordinal);
        return second < 0 ? string.Empty : line[start..second].Trim();
    }

    /// <summary>Группа записи. Неизвестные теги видны только в «Всё».</summary>
    public static string GroupOf(string line) => TagOf(line) switch
    {
        "tun-bypass" or "tun-bypass-core" or "desync" or "zapret" or "bypass"
            or "presets" or "pipe" => Bypass,
        "probe" or "health" or "speed" or "nodes" or "sources" or "discovery"
            or "discover" or "cache" or "traffic" or "stats" or "seed" => Probes,
        "mtproto" => MtProto,
        "update" or "maintenance" => Updates,
        _ => All,
    };

    public static bool Matches(string line, string category)
    {
        if (string.IsNullOrEmpty(category) || category == All)
        {
            return true;
        }

        return category == Errors ? IsError(line) : GroupOf(line) == category;
    }

    /// <summary>Строка с исключением или необработанной ошибкой (у AppLog стек идёт после « | »).</summary>
    public static bool IsError(string line) =>
        line.Contains(" ⏎ ", StringComparison.Ordinal) ||
        line.Contains("Exception", StringComparison.Ordinal) ||
        line.Contains("Необработанная ошибка", StringComparison.Ordinal);
}
