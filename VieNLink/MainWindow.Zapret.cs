using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VieNLink;

public partial class MainWindow
{
    // ---------- обход DPI (zapret) ----------

    private const int StrategyRows = 5;

    /// <summary>
    /// Список стратегий — только для информации. Порядок автоматический: лучшие по
    /// накопленному качеству сверху, остальные — по истории удачных запусков.
    /// </summary>
    private void BuildZapretPresetList()
    {
        if (ZapretPresetPanel is null)
        {
            return;
        }

        ZapretPresetPanel.Children.Clear();
        var order = ZapretService.GetPriorityOrder();
        var columns = Math.Max(1, (order.Count + StrategyRows - 1) / StrategyRows);
        ZapretPresetPanel.Rows = StrategyRows;
        ZapretPresetPanel.Columns = columns;

        // UniformGrid заполняет построчно, а нужен «столбиками» по 5: раскладываем
        // элементы в массив по колонкам и добавляем в построчном порядке.
        var cells = new TextBlock?[StrategyRows * columns];
        for (var position = 0; position < order.Count; position++)
        {
            var index = order[position];
            var preset = ZapretService.Presets[index];
            var item = new TextBlock
            {
                Tag = (Index: index, Number: position + 1),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 12, 1),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = preset.Name,
            };
            cells[(position % StrategyRows) * columns + position / StrategyRows] = item;
        }

        for (var cell = 0; cell < cells.Length; cell++)
        {
            ZapretPresetPanel.Children.Add(cells[cell] ?? new TextBlock { Visibility = Visibility.Hidden });
        }

        UpdateZapretStrategyInfo(_settings.ZapretPreset);
    }

    private void UpdateZapretStrategyInfo(int index)
    {
        if (ZapretPresetPanel is null)
        {
            return;
        }

        var green = (Brush)FindResource("AccentBrush");
        var muted = (Brush)FindResource("MutedStrongBrush");
        foreach (var item in ZapretPresetPanel.Children.OfType<TextBlock>())
        {
            if (item.Tag is not (int presetIndex, int number) ||
                presetIndex < 0 || presetIndex >= ZapretService.Presets.Count)
            {
                continue;
            }

            var chosen = presetIndex == index;
            item.Text = $"{number}. {(chosen ? "★ " : string.Empty)}{ZapretService.Presets[presetIndex].Name}";
            item.Foreground = chosen ? green : muted;
            item.FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private static string Mark(bool ok) => ok ? "✓" : "—";

    private void OnZapretBlockQuicChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.ZapretBlockQuic = ZapretBlockQuicCheck.IsChecked == true;
        SaveSettings();
        if (_zapret.IsRunning)
        {
            ZapretTuneText.Text = "Блокировка QUIC применится после перезапуска обхода.";
        }
    }

    private void UpdateZapretUi()
    {
        if (ZapretStatusText is null)
        {
            return;
        }

        var installed = ZapretService.IsInstalled;
        var running = _zapret.IsRunning;
        var admin = IsAdministrator();
        var preset = _settings.ZapretPreset >= 0 && _settings.ZapretPreset < ZapretService.Presets.Count
            ? ZapretService.Presets[_settings.ZapretPreset].Name
            : "подберётся автоматически";
        ZapretStatusText.Text =
            $"Установлен: {(installed ? "да" : "нет")} · запущен: {(running ? "да" : "нет")} · " +
            $"права администратора: {(admin ? "есть" : "нет")} · стратегия: {preset}";

        if (ZapretActiveText is not null)
        {
            if (running)
            {
                ZapretActiveText.Foreground = (Brush)FindResource("AccentBrush");
                ZapretActiveText.Text = $"Сейчас применяется: «{preset}»";
            }
            else
            {
                ZapretActiveText.ClearValue(ForegroundProperty);
                ZapretActiveText.Text = "Обход DPI выключен — при включении стратегия подберётся сама.";
            }
        }

        if (BypassButton is not null)
        {
            BypassButton.Content = running ? "Остановить обход" : "Включить обход";
            BypassButton.Style = (Style)FindResource(running ? "DangerButton" : "PrimaryButton");
        }

        UpdateHeaderState();
        ZapretStateChanged?.Invoke();
    }

    internal event Action? ZapretStateChanged;

    internal bool ZapretRunning => _zapret.IsRunning;

    internal bool ZapretBusy => _busy;

    /// <summary>Строка состояния для виджета.</summary>
    internal string ZapretStatusLine
    {
        get
        {
            if (!ZapretService.IsInstalled)
            {
                return "zapret ещё не скачан — включите обход, файлы загрузятся сами.";
            }

            if (!_zapret.IsRunning)
            {
                return "Выключен. Включите обход — стратегия подберётся сама.";
            }

            var index = Math.Clamp(_settings.ZapretPreset, 0, ZapretService.Presets.Count - 1);
            return $"Включён: «{ZapretService.Presets[index].Name}»";
        }
    }

    internal int YoutubeNodeCount { get; private set; }

    internal int DiscordNodeCount { get; private set; }

    internal int UniversalNodeCount { get; private set; }

    internal int TelegramNodeCount { get; private set; }

    /// <summary>Флаг страны и уровень сигнала текущего подключения для свёрнутого виджета.</summary>
    internal string WidgetMiniInfoText
    {
        get
        {
            var node = _manager.CurrentNode;
            if (node is null || !_manager.IsConnected)
            {
                return "—";
            }

            var (flag, _) = ExtractCountryFlag(node.Name);
            var stars = RateConnection(node).Stars;
            var bars = stars switch
            {
                >= 5 => "▂▄▆█",
                4 => "▂▄▆",
                3 => "▂▄",
                2 => "▂",
                _ => "▁",
            };
            return (flag.Length == 0 ? "🌐" : flag) + " " + bars;
        }
    }

    internal bool IsServiceConnected(string service)
    {
        var node = _manager.CurrentNode;
        if (node is null || !_manager.IsConnected)
        {
            return false;
        }

        return service switch
        {
            "youtube" => node.YoutubeOk == true,
            "discord" => node.DiscordOk == true,
            "telegram" => node.TelegramOk == true,
            _ => node.UniversalOk == true || node.DelayMs is not null,
        };
    }

    internal async Task WidgetConnectServiceAsync(string service)
    {
        ApplyRoutingSettingsFromUi();
        SaveSettings();

        if (_settings.TunnelMode == TunnelMode.Tun && !IsAdministrator())
        {
            AskElevation();
            return;
        }

        var node = PickNodeForService(service);
        if (node is null)
        {
            var name = service switch
            {
                "youtube" => "YouTube",
                "telegram" => "Telegram",
                "discord" => "Discord",
                _ => "универсальным доступом",
            };
            MessageBox.Show(
                _zapretWidget is { IsVisible: true } ? (Window)_zapretWidget : this,
                $"Нет проверенных узлов с доступом: {name}.\n\n" +
                "Обновите списки и нажмите «Все узлы» на вкладке «Узлы».",
                "VieNLink",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await ConnectAsync(node);
    }

    internal Task WidgetToggleZapretAsync()
    {
        if (_zapret.IsRunning)
        {
            OnZapretStopClick(this, new RoutedEventArgs());
            return Task.CompletedTask;
        }

        return StartZapretFlowAsync(false, fromWidget: true);
    }

    internal Task WidgetTuneZapretAsync() => StartZapretFlowAsync(true, fromWidget: true);

    internal bool WidgetBusy => _busy;

    /// <summary>Свой обход DPI запущен — для виджета.</summary>
    internal bool DesyncRunning => _desync.IsRunning;

    /// <summary>Виджет: включить свой обход DPI (стратегия подберётся, если её ещё нет) или остановить.</summary>
    internal Task WidgetToggleDesyncAsync()
    {
        if (_desync.IsRunning)
        {
            OnDesyncToggleClick(this, new RoutedEventArgs());
            return Task.CompletedTask;
        }

        return StartDesyncFlowAsync(false, fromWidget: true);
    }

    /// <summary>Видео идёт через узел: подключены с маршрутизацией «только список» и в списке есть YouTube.</summary>
    internal bool VideoModeActive =>
        _manager.IsConnected &&
        _settings.SiteRouteMode == SiteRouteMode.OnlyListed &&
        _settings.SiteList.Contains("youtube.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Виджет: включить «Видео YouTube» через узел или выключить и вернуть весь трафик через узел.</summary>
    internal Task WidgetToggleVideoAsync() => VideoModeActive ? DisableVideoModeAsync() : VideoViaNodeAsync();

    /// <summary>Короткое имя работающего режима для виджета; пусто — ничего не включено.</summary>
    internal string ActiveModeName
    {
        get
        {
            if (VideoModeActive)
            {
                return "Видео YouTube";
            }

            if (_desync.IsRunning)
            {
                return "Свой обход DPI";
            }

            if (_zapret.IsRunning)
            {
                return "Обход запрет";
            }

            if (_tunBypass.IsRunning)
            {
                return "TUN-обход";
            }

            return _manager.IsConnected ? "Прокси-узел" : string.Empty;
        }
    }

    /// <summary>Подробность работающего режима: стратегия, пресет или имя узла.</summary>
    internal string ActiveModeDetail
    {
        get
        {
            if (VideoModeActive)
            {
                return ShortNodeName();
            }

            if (_desync.IsRunning)
            {
                return _desync.CurrentStrategy?.Name ?? string.Empty;
            }

            if (_zapret.IsRunning)
            {
                var index = Math.Clamp(_settings.ZapretPreset, 0, ZapretService.Presets.Count - 1);
                return ZapretService.Presets[index].Name;
            }

            if (_tunBypass.IsRunning)
            {
                return "TUN + форвардер";
            }

            if (_manager.IsConnected)
            {
                return ShortNodeName();
            }

            return string.Empty;
        }
    }

    /// <summary>Имя текущего узла для виджета — с обрезкой длинных названий.</summary>
    private string ShortNodeName()
    {
        var name = _manager.CurrentNode?.Name ?? "узел";
        return name.Length <= 32 ? name : name[..29] + "…";
    }

    /// <summary>Показать/скрыть плавающий виджет обхода DPI.</summary>
    internal void ToggleZapretWidget()
    {
        if (_zapretWidget is { IsVisible: true })
        {
            _zapretWidget.Close();
            UpdateWidgetToggleState();
            return;
        }

        if (_zapretWidget is null)
        {
            _zapretWidget = new ZapretWidget(this);
            _zapretWidget.Closed += (_, _) =>
            {
                _zapretWidget = null;
                UpdateWidgetToggleState();
            };
        }

        _zapretWidget.Show();
        _zapretWidget.Activate();
        UpdateWidgetToggleState();
    }

    /// <summary>Вкладка-кнопка «Виджет» показывает, открыт ли виджет.</summary>
    internal void UpdateWidgetToggleState()
    {
        if (WidgetTabChrome is null || WidgetToggleTrack is null || WidgetToggleKnob is null)
        {
            return;
        }

        var active = _zapretWidget is { IsVisible: true };
        // «Виджет» — не вкладка, а действие: подсвечиваем его акцентом всегда,
        // чтобы в общем ряду вкладок он читался как отдельная кнопка.
        WidgetTabChrome.Background = (Brush)FindResource("AccentSurfaceBrush");
        WidgetTabChrome.BorderBrush = (Brush)FindResource(active ? "AccentBrush" : "AccentBorderBrush");
        WidgetToggleTrack.Background = (Brush)FindResource(active ? "AccentBrush" : "MutedBrush");
        WidgetToggleKnob.HorizontalAlignment = active ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        WidgetToggleKnob.Margin = active ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0);
    }

    private async void OnBypassButtonClick(object sender, RoutedEventArgs e)
    {
        if (_zapret.IsRunning)
        {
            OnZapretStopClick(sender, e);
            return;
        }

        await StartZapretFlowAsync(false);
    }

    private async Task InstallZapretAsync()
    {
        if (!BeginOperation("загрузку zapret"))
        {
            return;
        }

        try
        {
            SetStatus("Скачиваю zapret", "winws + WinDivert с GitHub…");
            var progress = new Progress<string>(line =>
            {
                StatusDetail.Text = line;
                AppendLog(line);
            });
            await ZapretService.EnsureAsync(progress, _operation!.Token);
            SetStatus("zapret установлен", "Нажмите «Включить обход DPI»");
            AppendLog("zapret установлен: " + ZapretService.Directory);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Загрузка zapret отменена", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось скачать zapret", exception);
        }
        finally
        {
            EndOperation();
            UpdateZapretUi();
        }
    }

    private async Task StartZapretFlowAsync(bool forceTune, bool fromWidget = false)
    {
        if (!ZapretService.IsInstalled)
        {
            await InstallZapretAsync();
        }

        if (!ZapretService.IsInstalled)
        {
            return;
        }

        if (!IsAdministrator())
        {
            AskElevation(
                "Обход DPI (zapret) работает с сетевыми пакетами Windows и требует прав администратора.\n\n" +
                (forceTune
                    ? "Перезапустить VieNLink с повышением прав и подобрать стратегию под провайдера?"
                    : "Перезапустить VieNLink с повышением прав? Списки и настройки сохранятся."),
                (forceTune ? "--zapret-tune" : "--zapret") + (fromWidget ? " --widget" : string.Empty));
            return;
        }

        await StartZapretAsync(forceTune);
    }

    private async Task StartZapretAsync(bool forceTune)
    {
        if (!BeginOperation(forceTune ? "подбор стратегии zapret" : "включение обхода zapret"))
        {
            return;
        }

        try
        {
            if (_desync.IsRunning)
            {
                _desync.Stop();
                _tunBypassStartedDesync = false;
                UpdateDesyncUi();
                AppendLog("Свой обход DPI остановлен: включается zapret");
            }

            if (_tunBypass.IsRunning)
            {
                await _tunBypass.StopAsync();
                UpdateTunBypassUi();
                AppendLog("TUN-обход остановлен: включается zapret");
            }

            if (_manager.IsConnected)
            {
                SetStatus("Отключаю прокси-узел", "Для zapret нужен прямой трафик…");
                await _manager.DisconnectAsync();
                UpdateConnectionUi();
            }

            var progress = new Progress<string>(line =>
            {
                StatusDetail.Text = line;
                AppendLog(line);
                if (ZapretActiveText is not null)
                {
                    ZapretActiveText.ClearValue(ForegroundProperty);
                    ZapretActiveText.Text = line;
                }
            });

            if (!forceTune)
            {
                TryApplyBestZapretProfile();
            }

            if (forceTune || _settings.ZapretPreset < 0)
            {
                SetStatus("Подбираю стратегию обхода",
                    $"Перебираю {ZapretService.Presets.Count} стратегий zapret: YouTube, t.me, Discord…");
                var results = await _zapret.AutoTuneAsync(progress, _operation!.Token);
                BypassProfileStore.RecordMany(results
                    .Where(result => result.Ok > 0)
                    .Select(result => ZapretProfile(result, false, _settings.ZapretBlockQuic)));
                var best = results
                    .Where(result => result.Ok > 0)
                    .OrderByDescending(result => result.YoutubeOk)
                    .ThenByDescending(result => result.Ok)
                    .ThenBy(result => result.AverageMs)
                    .FirstOrDefault();

                if (best is null)
                {
                    // Ни одна проверка не прошла: всё равно включаем сохранённую/первую стратегию,
                    // чтобы обход работал и дальше проверялся в фоне.
                    var fallback = Math.Clamp(_settings.ZapretPreset, 0, ZapretService.Presets.Count - 1);
                    ZapretTuneText.Text =
                        $"Автоподбор не нашёл рабочую стратегию — включена «{ZapretService.Presets[fallback].Name}».";
                    SetStatus("Автоподбор не удался", "Включена сохранённая стратегия. Если сайты не открываются — подключите прокси-узел.");
                    _settings.ZapretPreset = fallback;
                    forceTune = false;
                }
                else
                {
                    _settings.ZapretPreset = best.Index;
                    ZapretTuneText.Text =
                        $"Выбран: «{best.Name}» — YouTube {Mark(best.YoutubeOk)}, " +
                        $"Telegram {Mark(best.TelegramOk)}, Discord {Mark(best.DiscordOk)}";
                    AppendLog($"Автоподбор zapret: выбран «{best.Name}» ({best.Ok}/{best.Total})");
                }

                SaveSettings();
                BuildZapretPresetList();
                foreach (var result in results)
                {
                    AppLog.Write("zapret",
                        $"Автоподбор: {result.Name} — {result.Ok}/{result.Total} " +
                        $"(YouTube {Mark(result.YoutubeOk)}, Telegram {Mark(result.TelegramOk)}, " +
                        $"Discord {Mark(result.DiscordOk)}), {result.AverageMs} мс");
                }
            }

            var preset = Math.Clamp(_settings.ZapretPreset, 0, ZapretService.Presets.Count - 1);
            SetStatus("Включаю обход DPI", $"Стратегия: {ZapretService.Presets[preset].Name}");
            await _zapret.StartAsync(
                progress, _operation!.Token, preset, gameMode: false, blockQuic: _settings.ZapretBlockQuic);
            BypassProfileStore.Record(StartedProfile(
                "zapret", preset, ZapretService.Presets[preset].Name,
                false, _settings.ZapretBlockQuic));
            UpdateZapretStrategyInfo(preset);
            SetStatus("Обход DPI включён", "zapret запущен. Проверьте YouTube и t.me напрямую.");
            FooterText.Text = "zapret: обход DPI включён (без прокси-узла)";
        }
        catch (OperationCanceledException)
        {
            SetStatus("Запуск zapret отменён", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось включить обход DPI", exception);
        }
        finally
        {
            EndOperation();
            UpdateZapretUi();
        }
    }

    private void OnZapretWidgetClick(object sender, RoutedEventArgs e) => ToggleZapretWidget();

    /// <summary>«Виджет» в общем ряду вкладок открывает плавающее окно, не переключая вкладку.</summary>
    private void OnWidgetTabPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleZapretWidget();
    }

    private void OnMainTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WidgetTab?.IsSelected != true)
        {
            return;
        }

        // Клавиатурная навигация по вкладкам тоже не должна открывать пустую страницу.
        ToggleZapretWidget();
        MainTabs.SelectedItem = ToolsTab;
        ToolsTabs.SelectedItem = GeneralTab;
    }

    private void OnZapretStopClick(object sender, RoutedEventArgs e)
    {
        _zapret.Stop();
        SetStatus("Обход DPI остановлен", "Трафик снова идёт напрямую");
        UpdateZapretUi();
    }

}
