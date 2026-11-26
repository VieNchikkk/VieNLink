namespace VieNLink.Mobile;

/// <summary>
/// Итог подбора стратегии обхода. Одинаково используется экраном TUN и главным экраном,
/// чтобы тексты итога и правила применения были одни и те же.
/// </summary>
internal sealed record BypassAutotuneOutcome(
    AutotuneMode Mode,
    AutotuneResult? Page,
    AutotuneResult? Video,
    bool QuicProbed,
    bool QuicOk,
    bool Aborted)
{
    public string ModeLabel => Mode == AutotuneMode.Best ? "«Подобрать лучшую»" : "«Первая рабочая»";

    public bool HasPage => Page?.Strategy is not null;

    /// <summary>Страница и превью открылись — только такой результат считается подтверждённым.</summary>
    public bool Strong => HasPage && Page!.Strong;

    public bool HasVideo => Video?.Strategy is not null;

    /// <summary>Страница в одну строку: стратегия, домены, скорость.</summary>
    public string PageDetail => Page?.Strategy is null
        ? $"{ModeLabel}: ничего не найдено"
        : $"{ModeLabel}: {Page.Strategy.Name} — {Page.Score} из {Page.Total}" +
          (Page.Strong ? " (страница и превью)" : " (только страница)") +
          (Page.SpeedKbps > 0 ? $", {Page.SpeedKbps} КБ/с" : string.Empty);

    /// <summary>Видео в одну строку, честно: поток / только звук / не проверялось.</summary>
    public string VideoDetail => Video?.Strategy is null
        ? "не проверено — медиасервер не отдал байты"
        : Video.AudioOnly
            ? $"только звук — {Video.Strategy.Name}, {Video.SpeedKbps} КБ/с"
            : Video.SniOnly
                ? $"медиасервер отвечает — {Video.Strategy.Name}, поток не проверялся"
                : $"{Video.Strategy.Name} — {Video.Score} из {Video.Total}, {Video.SpeedKbps} КБ/с";

    /// <summary>Строка про QUIC для экрана «Данные обхода».</summary>
    public string QuicDetail => !QuicProbed
        ? "не проверялась"
        : QuicOk
            ? "проходит (оставляю через релей)"
            : "проба не прошла — решит реальный трафик";

    /// <summary>Короткий итог для всплывающего сообщения и строки состояния.</summary>
    public string Summary(string networkLabel)
    {
        if (Aborted)
        {
            return "Подбор прерван: сеть перестала отвечать.";
        }

        if (!HasPage)
        {
            return "Ни одна стратегия не открыла страницу YouTube.";
        }

        var summary = $"{networkLabel}: " +
                      (Mode == AutotuneMode.Best ? "лучшая" : "первая рабочая") +
                      $" {Page!.Strategy!.Name} ({Page.Score}/{Page.Total}" +
                      (Page.Strong ? ")" : ", превью не прошло)") +
                      (Page.SpeedKbps > 0 ? $", {Page.SpeedKbps} КБ/с" : string.Empty) +
                      (HasVideo
                          ? Video!.AudioOnly
                              ? $", видео не идёт — звук {Video.Strategy!.Name} ({Video.SpeedKbps} КБ/с)"
                              : Video.SniOnly
                                  ? $", видео: медиасервер отвечает ({Video.Strategy!.Name}), поток не проверялся"
                                  : $", видео {Video.Strategy!.Name} ({Video.Score}/{Video.Total}" +
                                    (Video.SpeedKbps > 0 ? $", {Video.SpeedKbps} КБ/с)" : ")")
                          : ", видео не проверено (0 байт)");
        return summary;
    }
}

/// <summary>
/// Подбор стратегии TUN-обхода без интерфейса: проверка страницы и видео, проба QUIC,
/// сохранение результата в пресет сети. Вызывается и с экрана TUN, и с главного экрана,
/// чтобы кнопка подбора работала одинаково в обоих местах.
/// </summary>
internal static class BypassAutotune
{
    /// <summary>
    /// Перебирает стратегии и применяет лучшую. Бросает <see cref="OperationCanceledException"/>
    /// при отмене: вызывающий сам решает, как показать «остановлен».
    /// </summary>
    public static async Task<BypassAutotuneOutcome> RunAsync(
        Android.Content.Context context,
        AutotuneMode mode,
        IProgress<AutotuneStep>? progress,
        CancellationToken cancellationToken)
    {
        // Известный пресет (включая зашитую базу оператора) проверяем первым.
        var preferredPage = Enum.TryParse<StreamFragmentMode>(
            NetworkPresets.GetPage(context), out var pageMode)
            ? pageMode
            : (StreamFragmentMode?)null;
        var preferredVideo = Enum.TryParse<StreamFragmentMode>(
            NetworkPresets.GetVideo(context), out var videoMode)
            ? videoMode
            : preferredPage;

        var page = await StrategyAutotune.RunAsync(
            AutotuneScope.Page, progress, cancellationToken, preferredPage, mode);
        AutotuneResult? video = null;
        var quicProbed = false;
        var quicOk = false;

        // Если страница не проверилась из-за обрыва сети — видео и QUIC не трогаем.
        if (!page.Aborted)
        {
            video = await StrategyAutotune.RunAsync(
                AutotuneScope.Video, progress, cancellationToken, preferredVideo, mode);

            // QUIC не зависит от TCP-стратегии: проверяем один раз и выставляем автоматически.
            var quicForwarder = new TlsFragmentForwarder();
            try
            {
                await quicForwarder.StartAsync(StreamFragmentMode.None);
                quicOk = await StrategyAutotune.ProbeQuicAsync(quicForwarder.Port, cancellationToken);
                quicProbed = true;
            }
            finally
            {
                await quicForwarder.StopAsync();
            }

            if (quicOk)
            {
                // Синтетическая проба Initial прошла — разрешаем QUIC через релей.
                NetworkPresets.SetBlockQuic(context, false);
            }
            // Если не прошла — настройку не меняем: решение примет реальный QUIC-трафик
            // (синтетическая проба ненадёжна, Google может молча отбросить чужой Initial).
        }

        if (page.Strategy is not null)
        {
            NetworkPresets.SetPage(context, page.Strategy.Mode.ToString());
        }

        // Видео-пресет меняем только если проба реально скачала поток. Иначе оставляем
        // прежний: подменять его страничной стратегией — значит врать «видео работает».
        if (video?.Strategy is not null)
        {
            NetworkPresets.SetVideo(context, video.Strategy.Mode.ToString());
        }

        // Подтверждение в базе — только когда открылись страница и превью.
        if (page.Strategy is not null && page.Strong)
        {
            NetworkPresets.RememberSuccess(
                context,
                page.Strategy.Mode.ToString(),
                video?.Strategy?.Mode.ToString() ?? NetworkPresets.GetVideo(context),
                NetworkPresets.GetBlockQuic(context),
                transport: video?.Strategy is not null && !video.SniOnly && !video.AudioOnly
                    ? "tcp"
                    : string.Empty,
                videoKbps: video?.Strategy is not null && !video.SniOnly && !video.AudioOnly
                    ? video.SpeedKbps
                    : 0);
        }

        return new BypassAutotuneOutcome(mode, page, video, quicProbed, quicOk, page.Aborted);
    }
}
