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
    // ---------- свой обход DPI (WinDivert) ----------

    private void BuildDesyncStrategyList()
    {
        DesyncStrategyPanel.Children.Clear();
        for (var index = 0; index < DesyncService.Strategies.Count; index++)
        {
            var strategy = DesyncService.Strategies[index];
            var radio = new RadioButton
            {
                Content = strategy.Name,
                GroupName = "DesyncStrategy",
                Tag = index,
                Style = (Style)FindResource("AppRadioButton"),
                Margin = new Thickness(0, 2, 12, 2),
                FontSize = 12.5,
                ToolTip = strategy.Description,
            };
            radio.Checked += OnDesyncStrategyChecked;
            DesyncStrategyPanel.Children.Add(radio);
        }

        SelectDesyncStrategyRadio(_settings.DesyncStrategy);
    }

    private void SelectDesyncStrategyRadio(int index)
    {
        var green = (Brush)FindResource("AccentBrush");
        foreach (var radio in DesyncStrategyPanel.Children.OfType<RadioButton>())
        {
            var isChosen = radio.Tag is int value && value == index;
            radio.IsChecked = isChosen;
            var name = radio.Tag is int tag && tag >= 0 && tag < DesyncService.Strategies.Count
                ? DesyncService.Strategies[tag].Name
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

    private void UpdateDesyncUi()
    {
        if (DesyncStatusText is null)
        {
            return;
        }

        var installed = DesyncService.IsInstalled;
        var running = _desync.IsRunning;
        var admin = IsAdministrator();
        var selected = _settings.DesyncStrategy >= 0 && _settings.DesyncStrategy < DesyncService.Strategies.Count
            ? DesyncService.Strategies[_settings.DesyncStrategy]
            : null;

        DesyncStatusText.Text =
            $"WinDivert: {(installed ? "установлен" : "не установлен")} · " +
            $"запущен: {(running ? "да" : "нет")} · " +
            $"права администратора: {(admin ? "есть" : "нет")} · " +
            $"стратегия: {selected?.Name ?? "не подобрана"}";
        if (selected is not null)
        {
            DesyncStatusText.Text += " — " + selected.Description;
        }

        if (DesyncActiveText is not null)
        {
            if (running)
            {
                DesyncActiveText.Foreground = (Brush)FindResource("AccentBrush");
                DesyncActiveText.Text = $"Сейчас применяется: «{_desync.CurrentStrategy?.Name}»";
            }
            else
            {
                DesyncActiveText.ClearValue(ForegroundProperty);
                DesyncActiveText.Text = "Свой обход DPI выключен — трафик идёт напрямую.";
            }
        }

        if (DesyncToggleButton is not null)
        {
            DesyncToggleButton.Content = running ? "Остановить обход" : "Включить обход";
            DesyncToggleButton.Style = (Style)FindResource(running ? "DangerButton" : "PrimaryButton");
        }

        if (DesyncInstallButton is not null)
        {
            DesyncInstallButton.Content = installed ? "Переустановить WinDivert" : "Установить WinDivert";
        }
    }

    private async void OnDesyncInstallClick(object sender, RoutedEventArgs e) => await InstallDesyncAsync();

    private async Task InstallDesyncAsync()
    {
        if (!BeginOperation())
        {
            return;
        }

        try
        {
            if (_desync.IsRunning)
            {
                _desync.Stop();
                UpdateDesyncUi();
            }

            SetStatus("Устанавливаю WinDivert", "DLL и драйвер в папку desync…");
            var progress = new Progress<string>(line =>
            {
                StatusDetail.Text = line;
                AppendLog(line);
            });
            await DesyncService.EnsureAsync(progress, _operation!.Token);
            SetStatus("WinDivert установлен", "Можно включать свой обход DPI");
            AppendLog("WinDivert установлен: " + DesyncService.Directory);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Установка WinDivert отменена", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось установить WinDivert", exception);
        }
        finally
        {
            EndOperation();
            UpdateDesyncUi();
        }
    }

    private async void OnDesyncToggleClick(object sender, RoutedEventArgs e)
    {
        if (_desync.IsRunning)
        {
            _desync.Stop();
            SetStatus("Свой обход DPI остановлен", "Трафик снова идёт напрямую");
            UpdateDesyncUi();
            return;
        }

        await StartDesyncFlowAsync(false);
    }

    private async void OnDesyncTuneClick(object sender, RoutedEventArgs e) => await StartDesyncFlowAsync(true);

    private async Task StartDesyncFlowAsync(bool forceTune, bool fromWidget = false)
    {
        if (!DesyncService.IsInstalled)
        {
            await InstallDesyncAsync();
        }

        if (!DesyncService.IsInstalled)
        {
            return;
        }

        if (!IsAdministrator())
        {
            AskElevation(
                "Собственный обход DPI работает с сетевыми пакетами через WinDivert и требует прав администратора.\n\n" +
                (forceTune
                    ? "Перезапустить VieNLink с повышением прав и подобрать стратегию?"
                    : "Перезапустить VieNLink с повышением прав? Списки и настройки сохранятся."),
                (forceTune ? "--desync-tune" : "--desync") + (fromWidget ? " --widget" : string.Empty));
            return;
        }

        await StartDesyncAsync(forceTune);
    }

    private async Task StartDesyncAsync(bool forceTune)
    {
        if (!BeginOperation())
        {
            return;
        }

        try
        {
            if (_manager.IsConnected)
            {
                SetStatus("Отключаю прокси-узел", "Для своего обхода DPI нужен прямой трафик…");
                await _manager.DisconnectAsync();
                UpdateConnectionUi();
            }

            if (_zapret.IsRunning)
            {
                _zapret.Stop();
                UpdateZapretUi();
                AppendLog("zapret остановлен: включается свой обход DPI");
            }

            if (_tunBypass.IsRunning)
            {
                await _tunBypass.StopAsync();
                UpdateTunBypassUi();
                AppendLog("TUN-обход остановлен: включается свой обход DPI");
            }

            // Прямое включение «Своего обхода DPI» забирает владение движком у TUN-обхода.
            _tunBypassStartedDesync = false;

            var progress = new Progress<string>(line =>
            {
                StatusDetail.Text = line;
                AppendLog(line);
                if (DesyncActiveText is not null)
                {
                    DesyncActiveText.ClearValue(ForegroundProperty);
                    DesyncActiveText.Text = line;
                }
            });

            if (forceTune || _settings.DesyncStrategy < 0)
            {
                SetStatus("Подбираю стратегию", $"Перебираю {DesyncService.Strategies.Count} стратегий: YouTube и Telegram…");
                var results = await _desync.AutoTuneAsync(progress, _operation!.Token, _settings.DesyncBlockQuic);
                var best = results
                    .Where(result => result.Ok > 0)
                    .OrderByDescending(result => result.VideoOk)
                    .ThenByDescending(result => result.YoutubeOk)
                    .ThenByDescending(result => result.Ok)
                    .ThenBy(result => result.AverageMs)
                    .FirstOrDefault();
                if (best is null)
                {
                    // Автоподбор не помог: уточняем, это IP-блок или SNI/TLS.
                    var youtube = await DesyncService.DiagnoseAsync("www.youtube.com", _operation!.Token);
                    var cloudflare = await DesyncService.DiagnoseAsync("cp.cloudflare.com", _operation!.Token);
                    var ipBlocked = youtube.Kind == BlockKind.Ip && cloudflare.Kind == BlockKind.Ok;
                    var tlsBlocked = youtube.Kind == BlockKind.Tls;

                    DesyncTuneText.Text = ipBlocked
                        ? "TCP до YouTube не устанавливается (IP-блок), а Cloudflare открывается. " +
                          "Обход по SNI здесь не поможет — подключитесь к узлу («Инструменты → Узлы → Подключить») или добавьте свою подписку."
                        : tlsBlocked
                            ? "YouTube блокируется на уровне SNI/TLS. Попробуйте стратегию «Фейк + multidisorder, TTL 128» " +
                              "или «TTL 64» вручную; если не поможет — вкладку «Обход запрет» (zapret) или узел."
                            : "Автоподбор не нашёл рабочую стратегию. Попробуйте «Обход запрет» (zapret) или подключитесь к узлу.";
                    SetStatus("Автоподбор не удался", ipBlocked
                        ? "YouTube блокируется по IP — нужен узел"
                        : "Обход не включён — попробуйте zapret или узел");
                    ShowDesyncResults(results, null);
                    return;
                }
                else
                {
                    _settings.DesyncStrategy = best.Index;
                    SelectDesyncStrategyRadio(best.Index);
                    DesyncTuneText.Text = best.VideoOk
                        ? $"Выбран: «{best.Name}» — YouTube {Mark(best.YoutubeOk)}, видео {Mark(best.VideoOk)}, " +
                          $"Telegram {Mark(best.TelegramOk)}"
                        : $"Выбран: «{best.Name}» — страница YouTube {Mark(best.YoutubeOk)}, видео {Mark(best.VideoOk)}. " +
                          "Если страница открывается, а видео нет — провайдер блокирует видео-CDН по IP; " +
                          "для видео — вкладка «В разработке».";
                    ShowDesyncResults(results, best.Index);
                    AppendLog($"Автоподбор своего обхода: выбран «{best.Name}» ({best.Ok}/{best.Total})");
                }

                SaveSettings();
            }

            var strategy = DesyncService.Strategies[Math.Clamp(_settings.DesyncStrategy, 0, DesyncService.Strategies.Count - 1)];
            SetStatus("Включаю свой обход DPI", $"Стратегия: {strategy.Name}");
            _desync.Start(strategy, progress, _settings.DesyncBlockQuic);
            SetStatus("Свой обход DPI включён",
                "Страница YouTube откроется напрямую; видео-CDН у этого провайдера блокируется по IP — для видео вкладка «В разработке»");
            FooterText.Text = "Свой обход DPI включён (WinDivert, без прокси-узла)";
        }
        catch (OperationCanceledException)
        {
            SetStatus("Запуск своего обхода отменён", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось включить свой обход DPI", exception);
        }
        finally
        {
            EndOperation();
            UpdateDesyncUi();
        }
    }

    private void ShowDesyncResults(IReadOnlyList<DesyncProbeResult> results, int? chosenIndex)
        => ShowDesyncResults(results, chosenIndex, DesyncResultsPanel);

    private void ShowDesyncResults(IReadOnlyList<DesyncProbeResult> results, int? chosenIndex, StackPanel panel)
    {
        panel.Children.Clear();
        var green = (Brush)FindResource("AccentBrush");
        var muted = (Brush)FindResource("MutedStrongBrush");
        foreach (var result in results
                     .OrderByDescending(item => item.YoutubeOk)
                     .ThenByDescending(item => item.Ok)
                     .ThenBy(item => item.AverageMs))
        {
            var text = $"{result.Name}: {result.Ok}/{result.Total} " +
                       $"(YouTube {Mark(result.YoutubeOk)}, видео {Mark(result.VideoOk)}, " +
                       $"Telegram {Mark(result.TelegramOk)}), {result.AverageMs} мс";
            var line = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = result.VideoOk || result.YoutubeOk ? green : muted,
            };
            if (result.Index == chosenIndex)
            {
                line.FontWeight = FontWeights.SemiBold;
                line.Foreground = green;
                text = "★ " + text;
            }

            line.Text = text;
            panel.Children.Add(line);
        }
    }

    private void OnDesyncStrategyChecked(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || sender is not RadioButton { Tag: int index })
        {
            return;
        }

        _settings.DesyncStrategy = index;
        SaveSettings();
        UpdateDesyncUi();
        if (_desync.IsRunning)
        {
            _desync.Stop();
            UpdateDesyncUi();
            _ = StartDesyncFlowAsync(false);
        }
    }

    private void OnDesyncAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.DesyncAutoStart = DesyncAutoStartCheck.IsChecked == true;
        SaveSettings();
    }

    private void OnDesyncBlockQuicChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.DesyncBlockQuic = DesyncBlockQuicCheck.IsChecked == true;
        SaveSettings();
        if (_desync.IsRunning)
        {
            DesyncTuneText.Text = "Блокировка QUIC применится после перезапуска обхода.";
        }
    }

    private async void OnDesyncProbeClick(object sender, RoutedEventArgs e)
    {
        if (!_desync.IsRunning)
        {
            SetStatus("Свой обход DPI выключен", "Сначала включите обход, затем проверьте");
            return;
        }

        if (!BeginOperation())
        {
            return;
        }

        try
        {
            SetStatus("Проверяю напрямую", "YouTube и t.me через свой обход DPI…");
            var index = _settings.DesyncStrategy;
            var name = _desync.CurrentStrategy?.Name ?? "текущая";
            var result = await DesyncService.ProbeAsync(index, name, _operation!.Token, probeVideo: true);
            var youtubeTcp = await DesyncService.TcpReachableAsync(
                "www.youtube.com", 443, TimeSpan.FromSeconds(4), _operation!.Token);
            var cloudflareTcp = await DesyncService.TcpReachableAsync(
                "cp.cloudflare.com", 443, TimeSpan.FromSeconds(4), _operation!.Token);
            var ipBlocked = !youtubeTcp && cloudflareTcp;
            DesyncTuneText.Text =
                $"Прямой доступ: YouTube {Mark(result.YoutubeOk)}, видео {Mark(result.VideoOk)}, " +
                $"Telegram {Mark(result.TelegramOk)} ({result.Ok}/{result.Total}, {result.AverageMs} мс)" +
                (ipBlocked
                    ? " · TCP до YouTube не проходит — провайдер блокирует по IP, нужен прокси-узел"
                    : string.Empty);
            StatusDetail.Text = DesyncTuneText.Text;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Проверка отменена", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось проверить прямой доступ", exception);
        }
        finally
        {
            EndOperation();
        }
    }

}
