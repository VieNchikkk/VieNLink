using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace VieNLink;

public partial class MainWindow
{
    // ---------- TUN-обход без узла (пользовательский TCP-форвардер) ----------

    private void BuildTunBypassStrategyList()
    {
        TunBypassStrategyPanel.Children.Clear();
        for (var index = 0; index < TlsFragmentForwarder.Strategies.Count; index++)
        {
            var strategy = TlsFragmentForwarder.Strategies[index];
            var radio = new RadioButton
            {
                Content = strategy.Name,
                GroupName = "TunBypassStrategy",
                Tag = index,
                Style = (Style)FindResource("AppRadioButton"),
                Margin = new Thickness(0, 2, 12, 2),
                FontSize = 12.5,
                ToolTip = strategy.Description,
            };
            radio.Checked += OnTunBypassStrategyChecked;
            TunBypassStrategyPanel.Children.Add(radio);
        }

        SelectTunBypassStrategyRadio(_settings.TunBypassStrategy);
    }

    private void SelectTunBypassStrategyRadio(int index)
    {
        var green = (Brush)FindResource("AccentBrush");
        foreach (var radio in TunBypassStrategyPanel.Children.OfType<RadioButton>())
        {
            var isChosen = radio.Tag is int value && value == index;
            radio.IsChecked = isChosen;
            var name = radio.Tag is int tag && tag >= 0 && tag < TlsFragmentForwarder.Strategies.Count
                ? TlsFragmentForwarder.Strategies[tag].Name
                : string.Empty;
            radio.Content = isChosen ? "★ " + name : name;
            if (isChosen)
            {
                radio.Foreground = green;
                radio.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                radio.ClearValue(ForegroundProperty);
                radio.ClearValue(FontWeightProperty);
            }
        }
    }

    private void UpdateTunBypassUi()
    {
        if (TunBypassStatusText is null)
        {
            return;
        }

        var running = _tunBypass.IsRunning;
        var desyncActive = !running && _tunBypassStartedDesync && _desync.IsRunning;
        var admin = IsAdministrator();
        var selected = _settings.TunBypassStrategy >= 0 &&
                       _settings.TunBypassStrategy < TlsFragmentForwarder.Strategies.Count
            ? TlsFragmentForwarder.Strategies[_settings.TunBypassStrategy]
            : null;
        TunBypassStatusText.Text = desyncActive
            ? "TUN-обход: включён WinDivert-движок (fake+multidisorder) · адаптер VieNLink-Bypass не используется"
            : $"TUN-обход: {(running ? "запущен" : "выключен")} · адаптер VieNLink-Bypass · " +
              $"QUIC: {(_settings.TunBypassBlockQuic ? "блокируется" : "напрямую")} · " +
              $"права администратора: {(admin ? "есть" : "нет")} · " +
              $"стратегия: {selected?.Name ?? "не выбрана"}";

        if (TunBypassActiveText is not null)
        {
            if (desyncActive)
            {
                TunBypassActiveText.Foreground = (Brush)FindResource("AccentBrush");
                TunBypassActiveText.Text = $"Сейчас применяется: «{_desync.CurrentStrategy?.Name ?? "фрагментация"}» · WinDivert";
            }
            else if (running)
            {
                var mode = TlsFragmentForwarder.Strategies
                    .FirstOrDefault(strategy => strategy.Mode == _tunBypass.Mode)?.Name ?? "фрагментация";
                TunBypassActiveText.Foreground = (Brush)FindResource("AccentBrush");
                TunBypassActiveText.Text = $"Сейчас применяется: «{mode}» · TCP через форвардер 127.0.0.1:{_tunBypass.ForwarderPort}";
            }
            else
            {
                TunBypassActiveText.ClearValue(ForegroundProperty);
                TunBypassActiveText.Text = "TUN-обход выключен — трафик снова идёт напрямую.";
            }
        }

        if (TunBypassToggleButton is not null)
        {
            TunBypassToggleButton.Content = running || desyncActive ? "Остановить TUN-обход" : "Включить TUN-обход";
            TunBypassToggleButton.Style = (Style)FindResource(running || desyncActive ? "DangerButton" : "PrimaryButton");
        }

        UpdateHeaderState();
    }

    private async void OnTunBypassToggleClick(object sender, RoutedEventArgs e)
    {
        if (_tunBypass.IsRunning || (_tunBypassStartedDesync && _desync.IsRunning))
        {
            await StopTunBypassAsync();
            return;
        }

        await StartTunBypassFlowAsync();
    }

    private async void OnTunBypassAutoConnectClick(object sender, RoutedEventArgs e)
    {
        if (_tunBypass.IsRunning || (_tunBypassStartedDesync && _desync.IsRunning))
        {
            await StopTunBypassAsync();
        }

        await StartTunBypassFlowAsync(forceTune: true);
    }

    private async Task StartTunBypassFlowAsync(bool forceTune = false)
    {
        if (!IsAdministrator())
        {
            AskElevation(
                "TUN-обход перехватывает трафик всех программ через виртуальный адаптер и требует прав администратора.\n\n" +
                "Перезапустить VieNLink с повышением прав? Списки и настройки сохранятся.",
                forceTune ? "--tun-bypass-tune" : "--tun-bypass");
            return;
        }

        await StartTunBypassAsync(forceTune);
    }

    private async Task StartTunBypassAsync(bool forceTune = false)
    {
        if (!BeginOperation(forceTune ? "автоподбор TUN-обхода" : "включение TUN-обхода"))
        {
            return;
        }

        try
        {
            // Всегда гасим ядро прокси: даже тестовое ядро в TUN-режиме заняло бы адаптер.
            SetStatus("Останавливаю прокси-ядро", "Для TUN-обхода нужен прямой трафик…");
            await _manager.DisconnectAsync();
            UpdateConnectionUi();

            if (_zapret.IsRunning)
            {
                _zapret.Stop();
                UpdateZapretUi();
                AppendLog("zapret остановлен: включается TUN-обход");
            }

            if (_desync.IsRunning)
            {
                _desync.Stop();
                UpdateDesyncUi();
                AppendLog("Свой обход DPI остановлен: включается TUN-обход");
            }

            _tunBypassStartedDesync = false;

            var index = Math.Clamp(_settings.TunBypassStrategy, 0, TlsFragmentForwarder.Strategies.Count - 1);
            var progress = new Progress<string>(line =>
            {
                StatusDetail.Text = line;
                AppendLog(line);
                if (TunBypassActiveText is not null)
                {
                    TunBypassActiveText.ClearValue(ForegroundProperty);
                    TunBypassActiveText.Text = line;
                }
            });

            if (!forceTune && TryApplyBestTunProfile(out var savedDesyncEngine))
            {
                if (savedDesyncEngine)
                {
                    var savedStrategy = DesyncService.Strategies[Math.Clamp(
                        _settings.DesyncStrategy, 0, DesyncService.Strategies.Count - 1)];
                    _desync.Start(savedStrategy, progress, _settings.DesyncBlockQuic);
                    _tunBypassStartedDesync = true;
                    SetStatus("Обход включён (WinDivert)", $"Стратегия: {savedStrategy.Name}");
                    FooterText.Text = "Обход включён (WinDivert, fake+multidisorder)";
                    AppendLog($"TUN-обход: сохранённый WinDivert-конфиг «{savedStrategy.Name}»");
                    UpdateTunBypassUi();
                    return;
                }
            }

            if (forceTune)
            {
                SetStatus("Подбираю стратегию TUN-обхода",
                    $"Перебираю {TlsFragmentForwarder.Strategies.Count} стратегий: YouTube и Telegram…");
                var results = await _tunBypass.AutoTuneAsync(_settings, progress, _operation!.Token);
                BypassProfileStore.RecordMany(results
                    .Where(result => result.YoutubeOk || result.TelegramOk)
                    .Select(result => TunProfile(result, _settings.TunBypassBlockQuic)));
                // «Рабочая» — та, что реально открыла заблокированную цель (YouTube или Telegram).
                var best = results
                    .Where(result => result.YoutubeOk || result.TelegramOk)
                    .OrderByDescending(result => result.YoutubeOk)
                    .ThenByDescending(result => result.TelegramOk)
                    .ThenByDescending(result => result.Ok)
                    .ThenBy(result => result.AverageMs)
                    .FirstOrDefault();

                // Форвардер умеет только разрезать ClientHello; fake+multidisorder (TTL, badseq,
                // TCP MD5, повторы) ему недоступны. Если цели не открылись и WinDivert установлен —
                // пробуем сильный движок, который это умеет.
                if (best is null && DesyncService.IsInstalled)
                {
                    SetStatus("Форвардер не открыл цели", "Пробую WinDivert-движок (fake+multidisorder)…");
                    var desyncResults = await _desync.AutoTuneAsync(progress, _operation!.Token, _settings.DesyncBlockQuic);
                    BypassProfileStore.RecordMany(desyncResults
                        .Where(result => result.Ok > 0)
                        .Select(result => DesyncProfile(result, _settings.DesyncBlockQuic, "tun-desync")));
                    var desyncBest = desyncResults
                        .Where(result => result.Ok > 0)
                        .OrderByDescending(result => result.VideoOk)
                        .ThenByDescending(result => result.YoutubeOk)
                        .ThenByDescending(result => result.Ok)
                        .ThenBy(result => result.AverageMs)
                        .FirstOrDefault();
                    if (desyncBest is not null && (desyncBest.YoutubeOk || desyncBest.VideoOk || desyncBest.TelegramOk))
                    {
                        _settings.DesyncStrategy = desyncBest.Index;
                        UpdateDesyncStrategyInfo(desyncBest.Index);
                        SaveSettings();
                        var desyncStrategy = DesyncService.Strategies[desyncBest.Index];
                        _desync.Start(desyncStrategy, progress, _settings.DesyncBlockQuic);
                        _tunBypassStartedDesync = true;
                        ShowDesyncResults(desyncResults, desyncBest.Index, TunBypassResultsPanel);
                        TunBypassTuneText.Text =
                            $"Автоподключение: выбран WinDivert-движок «{desyncStrategy.Name}» — " +
                            $"YouTube {Mark(desyncBest.YoutubeOk)}, видео {Mark(desyncBest.VideoOk)}, " +
                            $"Telegram {Mark(desyncBest.TelegramOk)}. TUN-форвардер (split) эти сайты не открывает: " +
                            "нужен fake+multidisorder.";
                        SetStatus("Обход включён (WinDivert)", $"Стратегия: {desyncStrategy.Name}");
                        FooterText.Text = "Обход включён (WinDivert, fake+multidisorder)";
                        AppendLog($"Автоподключение TUN-обхода: выбран WinDivert-движок «{desyncStrategy.Name}»");
                        return;
                    }
                }

                if (best is null)
                {
                    // Автоподбор не открыл ни один сайт: уточняем, IP-блок или SNI/TLS.
                    var youtube = await DesyncService.DiagnoseAsync("www.youtube.com", _operation!.Token);
                    var cloudflare = await DesyncService.DiagnoseAsync("cp.cloudflare.com", _operation!.Token);
                    var ipBlocked = youtube.Kind == BlockKind.Ip && cloudflare.Kind == BlockKind.Ok;
                    TunBypassTuneText.Text = ipBlocked
                        ? "Ни одна стратегия не открыла сайты, а TCP до YouTube не устанавливается (IP-блок), " +
                          "хотя Cloudflare открывается. Провайдер блокирует YouTube/t.me по IP, а не по SNI — " +
                          "обход по SNI здесь не поможет; для них нужен прокси-узел."
                        : "Ни одна стратегия не открыла сайты. Попробуйте «Обход запрет» (zapret) или подключитесь к узлу.";
                    SetStatus("Автоподключение не удалось", ipBlocked
                        ? "YouTube блокируется по IP — нужен узел"
                        : "TUN-обход не помог — попробуйте zapret или узел");
                    ShowTunBypassResults(results, null);
                    return;
                }

                _settings.TunBypassStrategy = best.Index;
                SelectTunBypassStrategyRadio(best.Index);
                SaveSettings();
                ShowTunBypassResults(results, best.Index);
                TunBypassTuneText.Text = $"Выбран: «{best.Name}» — YouTube {Mark(best.YoutubeOk)}, " +
                                         $"Telegram {Mark(best.TelegramOk)}.";

                AppendLog($"Автоподключение TUN-обхода: выбран «{best.Name}» ({best.Ok}/{best.Total})");
                index = best.Index;
            }

            var mode = TlsFragmentForwarder.Strategies[index].Mode;
            _tunBypassStartedDesync = false;
            SetStatus("Включаю TUN-обход", $"Стратегия: {TlsFragmentForwarder.Strategies[index].Name}");
            await _tunBypass.StartAsync(mode, _settings, progress, _operation!.Token);
            BypassProfileStore.Record(StartedProfile(
                "tun", index, TlsFragmentForwarder.Strategies[index].Name,
                gameMode: false, _settings.TunBypassBlockQuic));
            SetStatus("TUN-обход включён", "Проверьте YouTube и t.me — трафик идёт через форвардер VieNLink");
            FooterText.Text = "TUN-обход включён (без прокси-узла и WinDivert)";
        }
        catch (OperationCanceledException)
        {
            SetStatus("Запуск TUN-обхода отменён", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось включить TUN-обход", exception);
        }
        finally
        {
            EndOperation();
            UpdateTunBypassUi();
        }
    }

    private async Task StopTunBypassAsync()
    {
        try
        {
            if (_tunBypassStartedDesync && _desync.IsRunning)
            {
                _desync.Stop();
                UpdateDesyncUi();
                _tunBypassStartedDesync = false;
            }

            await _tunBypass.StopAsync();
            SetStatus("TUN-обход остановлен", "Трафик снова идёт напрямую");
        }
        catch (Exception exception)
        {
            AppLog.Write("tun-bypass", "Не удалось остановить TUN-обход", exception);
            SetStatus("TUN-обход остановлен с ошибкой", exception.Message);
        }
        finally
        {
            UpdateTunBypassUi();
        }
    }

    private async void OnTunBypassProbeClick(object sender, RoutedEventArgs e)
    {
        if (!_tunBypass.IsRunning)
        {
            SetStatus("TUN-обход выключен", "Сначала включите TUN-обход, затем проверьте");
            return;
        }

        if (!BeginOperation("проверку TUN-обхода"))
        {
            return;
        }

        try
        {
            SetStatus("Проверяю TUN-обход", "YouTube и t.me через отдельный процесс…");
            var result = await TunBypassService.ProbeAsync(_operation!.Token);
            var text = $"TUN-обход: YouTube {Mark(result.YoutubeOk)}, Telegram {Mark(result.TelegramOk)} " +
                       $"({result.Ok}/{result.Total}, {result.AverageMs} мс)";
            TunBypassTuneText.Text = text;
            StatusDetail.Text = text;
            if (result.Ok > 0)
            {
                BypassProfileStore.Record(_tunBypassStartedDesync
                    ? DesyncProfile(result, _settings.DesyncBlockQuic, "tun-desync")
                    : TunProfile(result, _settings.TunBypassBlockQuic));
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Проверка отменена", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось проверить TUN-обход", exception);
        }
        finally
        {
            EndOperation();
        }
    }

    private void OnTunBypassStrategyChecked(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || sender is not RadioButton { Tag: int index })
        {
            return;
        }

        _settings.TunBypassStrategy = index;
        SaveSettings();
        UpdateTunBypassUi();
        if (_tunBypass.IsRunning && !_busy)
        {
            _ = RestartTunBypassAsync();
        }
    }

    private async Task RestartTunBypassAsync()
    {
        await StopTunBypassAsync();
        await StartTunBypassFlowAsync();
    }

    /// <summary>Итоги автоподключения TUN-обхода: рабочие для YouTube/Telegram — зелёным, выбранная — со звёздочкой.</summary>
    private void ShowTunBypassResults(IReadOnlyList<DesyncProbeResult> results, int? chosenIndex)
    {
        TunBypassResultsPanel.Children.Clear();
        var green = (Brush)FindResource("AccentBrush");
        var muted = (Brush)FindResource("MutedStrongBrush");
        foreach (var result in results
                     .OrderByDescending(item => item.YoutubeOk)
                     .ThenByDescending(item => item.TelegramOk)
                     .ThenByDescending(item => item.Ok)
                     .ThenBy(item => item.AverageMs))
        {
            var text = $"{result.Name}: {result.Ok}/{result.Total} " +
                       $"(YouTube {Mark(result.YoutubeOk)}, Telegram {Mark(result.TelegramOk)}), {result.AverageMs} мс";
            var line = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = result.YoutubeOk || result.TelegramOk ? green : muted,
            };
            if (result.Index == chosenIndex)
            {
                line.FontWeight = FontWeights.SemiBold;
                line.Foreground = green;
                text = "★ " + text;
            }

            line.Text = text;
            TunBypassResultsPanel.Children.Add(line);
        }
    }

    private void OnTunBypassBlockQuicChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.TunBypassBlockQuic = TunBypassBlockQuicCheck.IsChecked == true;
        SaveSettings();
        if (_tunBypass.IsRunning)
        {
            TunBypassTuneText.Text = "QUIC применится после перезапуска TUN-обхода.";
        }
    }

    private void OnTunBypassAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.TunBypassAutoStart = TunBypassAutoStartCheck.IsChecked == true;
        SaveSettings();
    }

    /// <summary>
    /// Домены YouTube, которым нужен именно узел: видео-CDН (googlevideo) провайдер
    /// отдаёт с локального GGC и режет его по IP, поэтому обход по SNI там не помогает.
    /// </summary>
    private const string YouTubeProxyDomains =
        "youtube.com\nyoutu.be\ngooglevideo.com\nytimg.com\nggpht.com\nyoutube-nocookie.com\n" +
        "youtubei.googleapis.com\njnn-pa.googleapis.com\nyoutubeembeddedplayer.googleapis.com\n" +
        "youtube-ui.l.google.com\nwide-youtube.l.google.com\nyt-video-upload.l.google.com\nytimg.l.google.com";

    /// <summary>
    /// Для видео важна не задержка, а пропускная способность: если скорость замерялась,
    /// берём самый быстрый узел, иначе обычный выбор по задержке и маскировке.
    /// </summary>
    private ProxyNode? PickVideoNode()
    {
        var measured = _workingRows
            .Select(row => row.Node)
            .Where(node => node.DelayMs is not null &&
                           node.SpeedMbps is not null &&
                           (!_settings.ConnectOnlyMine || node.IsMine))
            .OrderByDescending(node => node.SpeedMbps)
            .ThenByDescending(node => node.IsMine)
            .ThenBy(node => node.DelayMs)
            .FirstOrDefault();
        return measured ?? PickBestNode();
    }

    /// <summary>
    /// Видео YouTube через узел, остальные сайты — напрямую. Провайдер блокирует
    /// локальный видео-узел Google по IP, поэтому SNI-обход там бессилен.
    /// </summary>
    private async Task VideoViaNodeAsync()
    {
        var node = PickVideoNode();
        if (node is null)
        {
            const string text = "Для видео нужен рабочий прокси-узел: видео-CDН YouTube у вашего провайдера " +
                                "блокируется по IP, обход по SNI там не работает.\n\n" +
                                "Обновите списки: «Инструменты → Источники», затем «Инструменты → Узлы → Все узлы».";
            SetStatus("Нет рабочих узлов", "Для видео нужен хотя бы один проверенный узел");
            MessageBox.Show(this, text, "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Сначала список, потом переключатель: обработчик читает значения из UI.
        SiteListBox.Text = YouTubeProxyDomains;
        RouteOnlyRadio.IsChecked = true;
        OnRoutingChanged(this, new RoutedEventArgs());
        AppendLog("Видео YouTube пойдёт через узел, остальные сайты — напрямую");

        await ConnectAsync(node, automatic: true, probeUrls: new[] { ProxyManager.YoutubeTestUrl });
        SetStatus("Видео через узел",
            "YouTube открывается через узел, остальные сайты идут напрямую. " +
            "Вернуть всё как было: «Инструменты → Туннель → Всё через узел».");
    }

    /// <summary>
    /// Выключить «Видео через узел»: отключить узел и вернуть маршрутизацию
    /// «всё через узел», как и советует подсказка на вкладке.
    /// </summary>
    private async Task DisableVideoModeAsync()
    {
        await _manager.DisconnectAsync();
        UpdateConnectionUi();
        RouteAllRadio.IsChecked = true;
        OnRoutingChanged(this, new RoutedEventArgs());
        SetStatus("Видео через узел выключено", "Узел отключён, маршрутизация: всё через узел");
        AppendLog("Видео через узел выключено: узел отключён");
    }

    private async void OnBlockDiagnoseClick(object sender, RoutedEventArgs e)
    {
        if (_manager.IsConnected || _desync.IsRunning || _zapret.IsRunning || _tunBypass.IsRunning)
        {
            MessageBox.Show(
                this,
                "Для чистой диагностики отключитесь от узла и остановите обход DPI " +
                "(zapret, «Свой обход DPI», TUN-обход). Иначе проверка покажет результат уже с обходом.",
                "VieNLink",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!BeginOperation("диагностику блокировки"))
        {
            return;
        }

        try
        {
            SetStatus("Диагностика блокировки", "TCP и TLS напрямую…");
            var hosts = new[]
            {
                "www.youtube.com",
                "discord.com",
                "x.com",
                "t.me",
                "www.google.com",
                "cp.cloudflare.com",
            };
            var lines = new List<string>();
            foreach (var host in hosts)
            {
                var diagnosis = await DesyncService.DiagnoseAsync(host, _operation!.Token);
                lines.Add(DescribeBlock(diagnosis));
            }

            BlockDiagnoseText.Text = string.Join(Environment.NewLine, lines);
            StatusDetail.Text = "Диагностика блокировки готова";
            AppendLog("Диагностика блокировки: " + string.Join(" | ", lines));
        }
        catch (OperationCanceledException)
        {
            SetStatus("Диагностика отменена", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось выполнить диагностику блокировки", exception);
        }
        finally
        {
            EndOperation();
        }
    }

    private static string DescribeBlock(BlockDiagnosis diagnosis) => diagnosis.Kind switch
    {
        BlockKind.Ok => $"{diagnosis.Host} ({diagnosis.Address}): TCP и TLS в порядке — блокировки на этом уровне нет",
        BlockKind.Ip => $"{diagnosis.Host} ({diagnosis.Address}): TCP timeout — блокировка по IP/порту",
        BlockKind.Tls => $"{diagnosis.Host} ({diagnosis.Address}): TCP есть, TLS обрывается — похоже на блокировку по SNI/TLS",
        _ => $"{diagnosis.Host}: DNS не разрешился",
    };

    /// <summary>Проверка туннеля по кнопке: YouTube и t.me через текущий узел.</summary>
    private async void OnCheckTunnelClick(object sender, RoutedEventArgs e)
    {
        if (!_manager.IsConnected)
        {
            MessageBox.Show(this, "Сначала подключитесь к узлу.", "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!BeginOperation("проверку туннеля"))
        {
            return;
        }

        try
        {
            SetStatus("Проверяю туннель", "YouTube и t.me через текущий узел…");
            var youtube = await _manager.VerifyThroughProxyAsync(_operation!.Token, "https://www.youtube.com/");
            var telegram = await _manager.VerifyThroughProxyAsync(_operation!.Token, "https://t.me/");
            StatusDetail.Text = $"Туннель: YouTube {(youtube ? "открылся" : "не открылся")}, t.me {(telegram ? "открылся" : "не открылся")}";
            UpdateDiagnostics();

            if (!youtube && _settings.AutoFailover && _manager.CurrentNode is { } current)
            {
                AppendLog("Проверка туннеля не прошла — запускаю переключение");
                await HandleUnhealthyNodeAsync(current.Name);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Проверка туннеля отменена", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось проверить туннель", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

}
