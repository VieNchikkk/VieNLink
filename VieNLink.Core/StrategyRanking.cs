namespace VieNLink;

/// <summary>
/// Правила выбора лучшей стратегии — чистые функции, без сети и Android:
/// их проверяет SelfTest, а автоподбор использует этот же код.
/// </summary>
internal static class StrategyRanking
{
    /// <summary>
    /// Страница: больше открывшихся доменов → «страница + превью» → выше скорость (КБ/с).
    /// При равных доменах и одинаковой «силе» выигрывает более быстрая.
    /// </summary>
    public static bool IsBetterPage(
        int score, bool strong, int speed,
        int bestScore, bool bestStrong, int bestSpeed) =>
        score > bestScore ||
        (score == bestScore &&
         ((strong && !bestStrong) || (strong == bestStrong && speed > bestSpeed)));

    /// <summary>
    /// Видео: настоящий поток важнее звука, звук важнее «медиасервер хотя бы отвечает»
    /// (SNI-путь); внутри одного уровня — по скорости. Ничего не заменяет уже найденный
    /// рабочий результат.
    /// </summary>
    public static bool IsBetterVideo(
        bool videoOk, bool audioOk, bool sniOk, int speed,
        bool bestVideo, bool bestAudio, bool bestSni, int bestSpeed) =>
        videoOk
            ? !bestVideo || speed > bestSpeed
            : audioOk
                ? !bestVideo && (!bestAudio || speed > bestSpeed)
                : sniOk && !bestVideo && !bestAudio && !bestSni;
}
