namespace VieNLink;

/// <summary>
/// Скоростной порог для счётчиков и выбора. Бесплатные серверы часто отвечают за 3–5 с:
/// формально они рабочие (сайт открылся), но для видео и обычных сайтов бесполезны.
/// Такие серверы не пропадают из списка и участвуют в подключении, если быстрых нет,
/// но число на значке сервиса их не обещает.
/// </summary>
internal static class NodeSpeedPolicy
{
    /// <summary>Задержка, выше которой сервер считается медленным, мс.</summary>
    public const int SlowDelayMs = 2000;

    /// <summary>Сервер достаточно быстрый: задержка не измерена либо не выше порога.</summary>
    public static bool IsFast(ProxyNode node) =>
        node.DelayMs is not { } delay || delay <= SlowDelayMs;
}
