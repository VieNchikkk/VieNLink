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

    /// <summary>
    /// Список стратегий — только для информации. Порядок автоматический: лучшие по
    /// накопленному качеству сверху, остальные — в обычном порядке перебора.
    /// </summary>
    private void BuildDesyncStrategyList()
    {
        if (DesyncStrategyPanel is null)
        {
            return;
        }

        DesyncStrategyPanel.Children.Clear();
        var order = DesyncService.GetPriorityOrder();
        var columns = Math.Max(1, (order.Count + StrategyRows - 1) / StrategyRows);
        DesyncStrategyPanel.Rows = StrategyRows;
        DesyncStrategyPanel.Columns = columns;

        var cells = new TextBlock?[StrategyRows * columns];
        for (var position = 0; position < order.Count; position++)
        {
            var index = order[position];
            var strategy = DesyncService.Strategies[index];
            var item = new TextBlock
            {
                Tag = (Index: index, Number: position + 1),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 12, 1),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = strategy.Description,
            };
            cells[(position % StrategyRows) * columns + position / StrategyRows] = item;
        }

        for (var cell = 0; cell < cells.Length; cell++)
        {
            DesyncStrategyPanel.Children.Add(cells[cell] ?? new TextBlock { Visibility = Visibility.Hidden });
        }

        UpdateDesyncStrategyInfo(_settings.DesyncStrategy);
    }

    private void UpdateDesyncStrategyInfo(int index)
    {
        if (DesyncStrategyPanel is null)
        {
            return;
        }

        var green = (Brush)FindResource("AccentBrush");
        var muted = (Brush)FindResource("MutedStrongBrush");
        foreach (var item in DesyncStrategyPanel.Children.OfType<TextBlock>())
        {
            if (item.Tag is not (int strategyIndex, int number) ||
                strategyIndex < 0 || strategyIndex >= DesyncService.Strategies.Count)
            {
                continue;
            }

            var chosen = strategyIndex == index;
            item.Text = $"{number}. {(chosen ? "★ " : string.Empty)}{DesyncService.Strategies[strategyIndex].Name}";
            item.Foreground = chosen ? green : muted;
            item.FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal;
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
            $"стратегия: {selected?.Name ?? "подберётся автоматически"}";
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

        UpdateHeaderState();
    }

    private async Task InstallDesyncAsync()
    {
        if (!BeginOperation("установку WinDivert"))
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
        if (!BeginOperation(forceTune ? "подбор стратегии обхода DPI" : "включение обхода DPI"))
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

            if (!forceTune)
            {
                TryApplyBestDesyncProfile();
            }

            if (forceTune || _settings.DesyncStrategy < 0)
            {
                SetStatus("Подбираю стратегию", $"Перебираю {DesyncService.Strategies.Count} стратегий: YouTube и Telegram…");
                var results = await _desync.AutoTuneAsync(progress, _operation!.Token, _settings.DesyncBlockQuic);
                BypassProfileStore.RecordMany(results
                    .Where(result => result.Ok > 0)
                    .Select(result => DesyncProfile(result, _settings.DesyncBlockQuic, "desync")));
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
                            ? "YouTube блокируется на уровне SNI/TLS. Автоподбор попробовал все стратегии; " +
                              "если не помогло — включите zapret на вкладке «Обход Zapret» или подключитесь к узлу."
                            : "Автоподбор не нашёл рабочую стратегию. Попробуйте «Обход Zapret» или подключитесь к узлу.";
                    SetStatus("Автоподбор не удался", ipBlocked
                        ? "YouTube блокируется по IP — нужен узел"
                        : "Обход не включён — попробуйте zapret или узел");
                    return;
                }
                else
                {
                    _settings.DesyncStrategy = best.Index;
                    DesyncTuneText.Text = best.VideoOk
                        ? $"Выбран: «{best.Name}» — YouTube {Mark(best.YoutubeOk)}, видео {Mark(best.VideoOk)}, " +
                          $"Telegram {Mark(best.TelegramOk)}"
                        : $"Выбран: «{best.Name}» — страница YouTube {Mark(best.YoutubeOk)}, видео {Mark(best.VideoOk)}. " +
                          "Если страница открывается, а видео нет — включите блокировку QUIC или подключите прокси-узел.";
                    AppendLog($"Автоподбор своего обхода: выбран «{best.Name}» ({best.Ok}/{best.Total})");
                }

                SaveSettings();
                BuildDesyncStrategyList();
            }

            var strategy = DesyncService.Strategies[Math.Clamp(_settings.DesyncStrategy, 0, DesyncService.Strategies.Count - 1)];
            SetStatus("Включаю свой обход DPI", $"Стратегия: {strategy.Name}");
            _desync.Start(strategy, progress, _settings.DesyncBlockQuic);
            BypassProfileStore.Record(StartedProfile(
                "desync", Math.Clamp(_settings.DesyncStrategy, 0, DesyncService.Strategies.Count - 1),
                strategy.Name, gameMode: false, _settings.DesyncBlockQuic));
            SetStatus("Свой обход DPI включён",
                "Стратегия подобрана автоматически. Если видео не идёт — включите блокировку QUIC или прокси-узел.");
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

}
