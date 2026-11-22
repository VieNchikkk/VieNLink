namespace VieNLink;

/// <summary>
/// Единое правило «сервер можно предлагать прямо сейчас». Счётчики сервисов в шапке
/// и кнопки быстрого подключения обязаны считать одинаково: иначе число на значке
/// обещает сервер, которого кнопка не находит. Причина расхождения — кэш: сервер,
/// проверенный много часов назад, остаётся «рабочим» в списке, хотя уже умер.
/// </summary>
internal static class NodeAvailability
{
    /// <summary>Границы окна свежести в минутах: те же, что у фоновой перепроверки.</summary>
    public const int MinRecheckMinutes = 5;

    /// <summary>Верхняя граница окна свежести в минутах (неделя).</summary>
    public const int MaxRecheckMinutes = 168 * 60;

    /// <summary>Проверка не старше <paramref name="recheckMinutes"/> — результат ещё можно показывать.</summary>
    public static bool IsFresh(ProxyNode node, int recheckMinutes, DateTime nowUtc) =>
        node.CheckedAtUtc is { } checkedAt &&
        checkedAt >= nowUtc.AddMinutes(-Math.Clamp(recheckMinutes, MinRecheckMinutes, MaxRecheckMinutes));

    /// <summary>
    /// Момент, раньше которого рабочий сервер пора перепроверить: на
    /// <see cref="NodeLimits.RecheckLeadMinutes"/> раньше истечения окна свежести.
    /// Так сервер успевает попасть в партию до того, как счётчик перестанет его учитывать.
    /// </summary>
    public static DateTime RecheckCutoffUtc(DateTime nowUtc) =>
        nowUtc.AddMinutes(-Math.Max(1, NodeLimits.WorkingRecheckMinutes - NodeLimits.RecheckLeadMinutes));

    /// <summary>
    /// Есть хотя бы один подтверждённый успех: задержка или отметка сервиса. На Android
    /// задержка — это только Cloudflare, поэтому сервер, открывший лишь YouTube/Telegram,
    /// тоже считается проверенным (но не универсальным).
    /// </summary>
    public static bool HasSuccess(ProxyNode node) =>
        node.DelayMs is not null ||
        node.UniversalOk == true ||
        node.YoutubeOk == true ||
        node.TelegramOk == true ||
        node.DiscordOk == true;

    /// <summary>
    /// Годится для подключения прямо сейчас: проверен, жив, проверка свежая
    /// и фильтр «только мои» соблюдён. Окно свежести — в минутах и равно интервалу
    /// перепроверки (<see cref="NodeLimits.WorkingRecheckMinutes"/>): счётчик и кнопка
    /// обязаны считать одинаково.
    /// </summary>
    public static bool IsUsable(ProxyNode node, int recheckMinutes, bool onlyMine, DateTime nowUtc) =>
        node.Status == NodeStatus.Working &&
        HasSuccess(node) &&
        (!onlyMine || node.IsMine) &&
        IsFresh(node, recheckMinutes, nowUtc);

    /// <summary>
    /// Открывает ли сервер сервис. Ключи — как в тегах кнопок Windows:
    /// <c>youtube</c>, <c>telegram</c>, <c>discord</c>, всё остальное — универсальный доступ
    /// (проверочный адрес Cloudflare), а не «хоть что-нибудь открылось».
    /// </summary>
    public static bool OpenedFor(ProxyNode node, string service)
    {
        // Сравнение без ToLowerInvariant: счётчики в шапке перебирают тысячи серверов
        // несколько раз в секунду, и новые строки на каждый вызов сильно грузили GC.
        if (string.Equals(service, "youtube", StringComparison.OrdinalIgnoreCase))
        {
            return node.YoutubeOk == true;
        }

        if (string.Equals(service, "telegram", StringComparison.OrdinalIgnoreCase))
        {
            return node.TelegramOk == true;
        }

        if (string.Equals(service, "discord", StringComparison.OrdinalIgnoreCase))
        {
            return node.DiscordOk == true;
        }

        return node.UniversalOk == true;
    }
}
