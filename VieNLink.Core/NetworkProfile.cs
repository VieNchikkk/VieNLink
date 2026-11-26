namespace VieNLink;

/// <summary>
/// Сетевой режим платформы. На мобильной сети проверки рабочих серверов идут реже:
/// байты у проверки крошечные, но каждое пробуждение радиомодуля дороже самих запросов.
/// Значение выставляет платформа при старте и при смене сети; ПК всегда считает себя
/// проводным (Wi-Fi/Ethernet) — мобильный режим там не определить.
/// </summary>
internal static class NetworkProfile
{
    /// <summary>Активна мобильная сеть (Wi-Fi и провод выключены).</summary>
    public static bool IsMobile { get; set; }

    /// <summary>Сколько минут проверка считается свежей.</summary>
    public static int WorkingRecheckMinutes =>
        IsMobile ? NodeLimits.WorkingRecheckMinutesMobile : NodeLimits.WorkingRecheckMinutes;

    /// <summary>
    /// До какого момента рабочий сервер считается проверенным. Перепроверка начинается
    /// за <see cref="NodeLimits.RecheckLeadMinutes"/> до истечения окна: такт фона — минута,
    /// и без запаса счётчики на секунды обнулялись бы между партиями.
    /// </summary>
    public static DateTime RecheckCutoffUtc(DateTime nowUtc) =>
        nowUtc.AddMinutes(-Math.Max(1, WorkingRecheckMinutes - NodeLimits.RecheckLeadMinutes));
}
