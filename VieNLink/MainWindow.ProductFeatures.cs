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
    // ---------- шапка, статистика, обновление ядра и интеграция с Windows ----------

    /// <summary>Главное действие на первом экране: подключиться к лучшему узлу или отключиться.</summary>
    private void OnHeaderConnectClick(object sender, RoutedEventArgs e)
    {
        if (_manager.IsConnected || _manager.IsKillSwitchHeld)
        {
            _ = DisconnectAsync();
            return;
        }

        OnConnectSelectedClick(sender, e);
    }

    /// <summary>Отсчёт приходит из фонового потока: в UI переносим через диспетчер.</summary>
    private void OnTrafficUpdated(TrafficSample sample)
    {
        if (!_settings.ShowTraffic || _closing)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_manager.IsConnected)
            {
                return;
            }

            HeaderStatsText.Text =
                $"↓ {FormatSpeed(sample.DownBytesPerSecond)} · ↑ {FormatSpeed(sample.UpBytesPerSecond)}" +
                $"  ·  {FormatBytes(sample.TotalDown + sample.TotalUp)}";
            HeaderStatsText.Visibility = Visibility.Visible;
        });
    }

    /// <summary>Состояние кнопки подключения и подписей в шапке.</summary>
    private void UpdateHeaderState()
    {
        var connected = _manager.IsConnected;
        var held = _manager.IsKillSwitchHeld;
        ConnectButton.Content = connected ? "Отключиться" : held ? "Вернуть интернет" : "Подключить";
        ConnectButton.Style = (Style)FindResource(connected || held ? "DangerButton" : "PrimaryButton");
        ConnectButton.ToolTip = connected
            ? "Вернуть системный прокси и остановить ядро"
            : held
                ? "Kill switch удерживает трафик: вернуть настройки прокси Windows"
                : "Подключиться к выбранному или лучшему проверенному узлу";

        if (!connected)
        {
            HeaderStatsText.Visibility = Visibility.Collapsed;
            HeaderStatsText.Text = "↓ 0 КБ/с · ↑ 0 КБ/с";
            HeaderExitText.Visibility = Visibility.Collapsed;
            HeaderExitText.Text = string.Empty;
        }
        else if (_settings.ShowTraffic)
        {
            HeaderStatsText.Visibility = Visibility.Visible;
        }
        else
        {
            HeaderStatsText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Страна и IP выхода: один запрос через тот же узел, что и браузер.</summary>
    private async Task RefreshExitInfoAsync()
    {
        if (!_settings.ShowExitInfo)
        {
            HeaderExitText.Visibility = Visibility.Collapsed;
            return;
        }

        HeaderExitText.Text = "Выход: определение…";
        HeaderExitText.Visibility = Visibility.Visible;
        try
        {
            var info = await ExitInfoService.LookupAsync(_manager.ActiveMixedPort, CancellationToken.None);
            if (!_manager.IsConnected || _closing)
            {
                return;
            }

            HeaderExitText.Text = info is null ? "Выход: не определён" : "Выход: " + info.Label;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Write("exit", "Не удалось определить выходной IP", exception);
            HeaderExitText.Text = "Выход: не определён";
        }
    }

    /// <summary>Пресеты сайтов: добавляют домены в список и включают режим «только сайты из списка».</summary>
    private void OnSitePresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string domains } box)
        {
            return;
        }

        var preset = SplitDomains(domains);
        var list = SiteListBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (box.IsChecked == true)
        {
            foreach (var domain in preset)
            {
                if (!list.Contains(domain, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(domain);
                }
            }

            RouteOnlyRadio.IsChecked = true;
            FooterText.Text = $"«{box.Content}» добавлен: через узел пойдут только эти домены";
        }
        else
        {
            // Домен мог остаться нужен другому включённому пресету — его не убираем.
            var stillNeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (box.Parent is Panel panel)
            {
                foreach (var other in panel.Children.OfType<CheckBox>())
                {
                    if (!ReferenceEquals(other, box) && other.IsChecked == true && other.Tag is string otherDomains)
                    {
                        foreach (var domain in SplitDomains(otherDomains))
                        {
                            stillNeeded.Add(domain);
                        }
                    }
                }
            }

            list.RemoveAll(domain => preset.Contains(domain, StringComparer.OrdinalIgnoreCase) &&
                                     !stillNeeded.Contains(domain));
            FooterText.Text = $"«{box.Content}» убран из списка";
        }

        SiteListBox.Text = string.Join(Environment.NewLine, list);
        OnRoutingChanged(this, new RoutedEventArgs());
    }

    private static List<string> SplitDomains(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        var enabled = AutostartCheck.IsChecked == true;
        try
        {
            AutostartService.SetEnabled(enabled);
            _settings.RunAtStartup = enabled;
            SaveSettings();
            FooterText.Text = enabled ? "VieNLink будет запускаться при входе в Windows" : "Автозапуск выключен";
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                             or System.Security.SecurityException
                                             or InvalidOperationException)
        {
            ReportError("Не удалось изменить автозапуск", exception);
        }
    }

    /// <summary>Замер скорости: выбранный узел или 10 лучших рабочих. Требует запущенного ядра.</summary>
    private async void OnSpeedTestClick(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation())
        {
            return;
        }

        try
        {
            var selected = SelectedNode();
            List<ProxyNode> targets;
            if (_manager.IsConnected)
            {
                // Переключать узел под живым соединением нельзя: замеряем только текущий.
                if (_manager.CurrentNode is not { } current)
                {
                    SetStatus("Замер скорости", "Нет активного узла");
                    return;
                }

                if (selected is not null && selected.Id != current.Id)
                {
                    SetStatus("Замер скорости", "При активном соединении можно замерить только текущий узел");
                }

                targets = new List<ProxyNode> { current };
            }
            else
            {
                targets = selected is not null
                    ? new List<ProxyNode> { selected }
                    : _workingRows
                        .Select(row => row.Node)
                        .Where(node => node.DelayMs is not null)
                        .OrderBy(node => node.DelayMs)
                        .Take(10)
                        .ToList();
            }

            if (targets.Count == 0)
            {
                SetStatus("Нет узлов для замера", "Сначала проверьте узлы: «Все узлы» на вкладке «Узлы»");
                return;
            }

            if (!_manager.IsConnected)
            {
                SetStatus("Замер скорости", "Поднимаю ядро для замера…");
                await _manager.PrepareTestCoreAsync(targets, null, _operation!.Token);
            }

            var done = 0;
            foreach (var node in targets)
            {
                _operation!.Token.ThrowIfCancellationRequested();
                done++;
                SetStatus("Замер скорости", $"{node.Name}: {done} из {targets.Count}");
                double? mbps = null;
                try
                {
                    mbps = await _manager.MeasureSpeedAsync(node, _operation.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Один проблемный узел не должен срывать замер остальных.
                    AppLog.Write("speed", $"Замер «{node.Name}» не удался", exception);
                }

                _rowIndex.TryGetValue(node.Id, out var row);
                row?.Refresh();
                AppendLog(mbps is null
                    ? $"Скорость «{node.Name}»: замер не удался"
                    : $"Скорость «{node.Name}»: {mbps:0.0} Мбит/с");
            }

            NodeSourceService.SaveCache(AllRows.Select(r => r.Node).ToList());
            SetStatus("Замер скорости завершён", $"Замерено узлов: {targets.Count}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Замер скорости отменён", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось замерить скорость", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    /// <summary>Проверка и обновление ядра sing-box до последнего релиза.</summary>
    private async void OnCheckCoreUpdateClick(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation())
        {
            return;
        }

        try
        {
            CoreUpdateText.Text = "Проверяю последнюю версию sing-box…";
            var latest = await SingBoxCore.CheckLatestVersionAsync(_operation!.Token);
            if (string.IsNullOrWhiteSpace(latest))
            {
                CoreUpdateText.Text = "Не удалось проверить: GitHub и зеркала не ответили.";
                return;
            }

            var installed = SingBoxCore.InstalledVersion;
            if (Version.TryParse(latest, out var latestVersion) &&
                Version.TryParse(installed, out var installedVersion) &&
                latestVersion <= installedVersion)
            {
                CoreUpdateText.Text = $"Установлена актуальная версия {installed}.";
                return;
            }

            var answer = MessageBox.Show(
                this,
                $"Доступна версия {latest}, установлена {installed}.\n\n" +
                "Скачать и заменить ядро? Соединение будет разорвано.",
                "VieNLink",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                CoreUpdateText.Text = $"Обновление до {latest} отменено.";
                return;
            }

            if (_manager.IsConnected)
            {
                await _manager.DisconnectAsync();
                UpdateConnectionUi();
                await Task.Delay(500);
            }

            var progress = new Progress<string>(line => CoreUpdateText.Text = line);
            await SingBoxCore.UpdateAsync(latest, progress, _operation.Token);
            CoreUpdateText.Text = $"Ядро обновлено до {latest}. Подключитесь заново.";
            UpdateCoreStatus();
        }
        catch (OperationCanceledException)
        {
            CoreUpdateText.Text = "Обновление ядра отменено.";
        }
        catch (Exception exception)
        {
            ReportError("Не удалось обновить ядро", exception);
            CoreUpdateText.Text = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>Приложения Магазина не видят 127.0.0.1 без снятия loopback-изоляции.</summary>
    private async void OnStoreAppsLoopbackClick(object sender, RoutedEventArgs e)
    {
        if (!IsAdministrator())
        {
            AskElevation(
                "Снятие loopback-изоляции для приложений Магазина требует прав администратора.\n\n" +
                "Перезапустить VieNLink с повышением прав?",
                "--store-loopback");
            return;
        }

        var (ok, message) = await WindowsIntegration.EnableStoreAppLoopbackAsync(CancellationToken.None);
        StoreLoopbackText.Text = message;
        FooterText.Text = message;
        if (!ok)
        {
            AppLog.Write("ui", "CheckNetIsolation: " + message);
        }
    }

    /// <summary>Выбор узла из меню трея.</summary>
    private void OnTrayNodeSelected(string nodeId)
    {
        var node = AllRows.Select(row => row.Node).FirstOrDefault(candidate => candidate.Id == nodeId);
        if (node is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (_busy)
                {
                    return;
                }

                Show();
                WindowState = WindowState.Normal;
                if (_manager.IsConnected && _manager.CurrentNode?.Id == node.Id)
                {
                    return;
                }

                await ConnectAsync(node);
            }
            catch (Exception exception)
            {
                AppLog.Write("tray", "Не удалось подключиться к узлу из трея", exception);
            }
        }));
    }

    /// <summary>Верхние узлы для меню трея: личные, быстрые и по скорости.</summary>
    private void UpdateTrayNodes()
    {
        if (_tray is null)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateTrayNodes);
            return;
        }

        var nodes = _workingRows
            .Select(row => row.Node)
            .Where(node => node.DelayMs is not null)
            .OrderByDescending(node => node.IsMine)
            .ThenByDescending(node => node.SpeedMbps ?? -1)
            .ThenBy(node => node.DelayMs)
            .Take(8)
            .ToList();
        _tray.UpdateNodes(nodes, _manager.CurrentNode?.Id);
    }

    private static string FormatSpeed(long bytesPerSecond) => bytesPerSecond switch
    {
        < 1024 => bytesPerSecond + " Б/с",
        < 1024 * 1024 => (bytesPerSecond / 1024.0).ToString("0") + " КБ/с",
        _ => (bytesPerSecond / 1048576.0).ToString("0.0") + " МБ/с",
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => bytes + " Б",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0") + " КБ",
        < 1024L * 1024 * 1024 => (bytes / 1048576.0).ToString("0.0") + " МБ",
        _ => (bytes / 1073741824.0).ToString("0.00") + " ГБ",
    };
}
