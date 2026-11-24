namespace VieNLink.Mobile;

/// <summary>
/// Бюджет ресурсов для проверки серверов. Телевизор (1,7 ГБ ОЗУ, 32-битный процесс,
/// слабый CPU) не должен молотить партии как телефон: меньше квоты, партии, потоки
/// и реже такты, иначе фоновая проверка занимает процессор и интерфейс с пульта
/// подтормаживает. На телефоне и планшете значения совпадают с общими.
/// </summary>
internal static class ProbeProfile
{
    private static bool Low => DeviceProfile.IsTelevision;

    /// <summary>Сколько «свежих» рабочих серверов держать наготове.</summary>
    public static int MinWorking => Low ? 6 : NodeLimits.MinWorkingNodes;

    /// <summary>Сколько рабочих серверов нужно на каждый сервис.</summary>
    public static int MinWorkingPerService => Low ? 4 : NodeLimits.MinWorkingPerService;

    /// <summary>Партия фоновой перепроверки рабочих серверов.</summary>
    public static int RecheckBatch => Low ? 8 : 20;

    /// <summary>Партия проверки, которую запускает :probe.</summary>
    public static int ForegroundBatch => Low ? 6 : 10;

    /// <summary>Одновременных проверок в процессе :probe.</summary>
    public static int ProbeConcurrency => Low ? 4 : 6;

    /// <summary>Стартовая партия, пока список пуст.</summary>
    public static int StartupBatch => Low ? 60 : 150;

    /// <summary>Такт фоновой проверки.</summary>
    public static TimeSpan TickInterval => TimeSpan.FromMinutes(Low ? 2 : 1);

    /// <summary>Как часто искать новые серверы, когда квота набрана.</summary>
    public static TimeSpan SearchInterval => TimeSpan.FromMinutes(Low ? 20 : 10);

    /// <summary>Как часто искать новые серверы, когда рабочих мало.</summary>
    public static TimeSpan SearchIntervalWhenFew => TimeSpan.FromMinutes(Low ? 5 : 2);

    /// <summary>Обновление списков источников, когда рабочих серверов мало.</summary>
    public static TimeSpan SourceRefreshWhenFew => TimeSpan.FromMinutes(Low ? 60 : 30);

    /// <summary>Такт автономного сбора MTProto.</summary>
    public static TimeSpan MtProtoTick => TimeSpan.FromMinutes(Low ? 5 : 2);

    /// <summary>Интервал фонового обслуживания (будильник).</summary>
    public static int MaintenanceIntervalMinutes => Low ? 60 : 30;
}
