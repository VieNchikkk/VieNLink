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
    // ---------- состояние и трей ----------

    private bool BeginOperation(string? action = null)
    {
        _idleCts?.Cancel();

        if (_busy)
        {
            // Автообновление и фон не должны молча съедать нажатие пользователя:
            // для ручных действий показываем, что именно сейчас выполняется.
            if (action is not null &&
                !action.StartsWith("авто", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Дождитесь окончания", "Сейчас выполняется: " + _operationLabel);
            }

            return false;
        }

        _busy = true;
        _operationLabel = action ?? "текущее действие";
        _operation = new CancellationTokenSource();
        SetBusy(true);
        return true;
    }

    /// <summary>
    /// Если сейчас идёт автообновление/автопоиск, останавливаем его и уступаем
    /// место действию пользователя. Ручную операцию не трогаем.
    /// </summary>
    private async Task<bool> TryTakeOverAutoAsync()
    {
        if (!_busy)
        {
            return true;
        }

        if (!_operationLabel.StartsWith("авто", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Дождитесь окончания", "Сейчас выполняется: " + _operationLabel);
            return false;
        }

        SetStatus("Останавливаю автообновление", "Освобождаю место для выбранного действия…");
        _operation?.Cancel();
        for (var attempt = 0; attempt < 60 && _busy && !_closing; attempt++)
        {
            await Task.Delay(100);
        }

        if (_busy)
        {
            SetStatus("Не удалось остановить", "Автообновление ещё завершается — повторите через минуту");
            return false;
        }

        return true;
    }

    private void EndOperation()
    {
        _busy = false;
        _operation?.Dispose();
        _operation = null;
        SetBusy(false);
        _operationLabel = "текущее действие";

        // Сбой сервера, замеченный во время проверки или другой операции, обрабатываем сейчас.
        if (_pendingFailoverNode is { } pending)
        {
            _pendingFailoverNode = null;
            Dispatcher.BeginInvoke(new Action(() => _ = HandleUnhealthyNodeAsync(pending)));
        }
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        BypassButton.IsEnabled = !busy;
        TestSelectedButton.IsEnabled = !busy;
        TestBatchButton.IsEnabled = !busy;
        TestAllButton.IsEnabled = !busy;
        DesyncToggleButton.IsEnabled = !busy;
        TunBypassToggleButton.IsEnabled = !busy;
        TunBypassProbeButton.IsEnabled = !busy;
        BlockDiagnoseButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
        UpdateHeaderState();        ZapretStateChanged?.Invoke();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        FooterText.Text = "Останавливаю: " + _operationLabel;
    }

    private void SetStatus(string status, string detail)
    {
        StatusText.Text = status;
        StatusDetail.Text = detail;
        if (_manager.IsConnected)
        {
            StatusDot.Fill = (Brush)FindResource("AccentBrush");
        }
    }

    private void ReportError(string title, Exception exception)
    {
        AppLog.Write("ui", title, exception);
        StatusText.Text = title;
        StatusDetail.Text = exception.Message;
        StatusDot.Fill = (Brush)FindResource("DangerBrush");
        FooterText.Text = exception.Message;
        _tray?.ShowBalloon(title, exception.Message, warning: true);
    }

    private void InitializeTray()
    {
        _tray = new TrayIconService();
        _tray.ShowRequested += () =>
        {
            _idleCts?.Cancel();
            Show();
            WindowState = WindowState.Normal;
            Activate();
        };
        _tray.WidgetRequested += ToggleZapretWidget;
        _tray.NodeSelected += OnTrayNodeSelected;
        _tray.ToggleRequested += async () =>
        {            try
            {
                if (_manager.IsConnected || _manager.IsKillSwitchHeld)
                {
                    await DisconnectAsync();
                    return;
                }

                var node = PickBestNode();
                if (node is null)
                {
                    _tray.ShowBalloon("Нет серверов", "Обновите списки в окне VieNLink.", warning: true);
                    return;
                }

                await ConnectAsync(node);
            }
            catch (Exception exception)
            {
                AppLog.Write("tray", "Ошибка переключения из трея", exception);
            }
        };
        _tray.ExitRequested += () =>
        {
            _exitRequested = true;
            Close();
        };
    }

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Перезапуск с повышением прав через UAC. Настройки и списки сохраняются на диск.</summary>
    /// <summary>
    /// Показать окно по повторному запуску (закреплённая кнопка на панели задач,
    /// ярлык в «Пуске», второй экземпляр). Приложение обычно висит в трее.
    /// </summary>
    internal void ShowFromTray()
    {
        if (_exitRequested)
        {
            return;
        }

        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        // Кратковременный Topmost поднимает окно поверх остальных,
        // не оставляя его «всегда сверху».
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private static bool TryRestartElevated(string argument = "--tunnel")
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var arguments = argument;
            if (Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                // Запуск через dotnet (dotnet VieNLink.dll / dotnet run): перезапускаем тот же DLL.
                // Assembly.Location в single-file всегда пуст, поэтому имя берём из GetName(),
                // а каталог — из AppContext.BaseDirectory.
                var assemblyName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
                if (string.IsNullOrEmpty(assemblyName))
                {
                    return false;
                }

                var assembly = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
                if (!File.Exists(assembly))
                {
                    return false;
                }

                arguments = "\"" + assembly + "\" " + argument;
            }

            Process.Start(new ProcessStartInfo(path, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Пользователь отказался от UAC.
            return false;
        }
    }

    private void AskElevation(string? message = null, string argument = "--tunnel")
    {
        var owner = _zapretWidget is { IsVisible: true } ? (Window)_zapretWidget : this;
        var answer = MessageBox.Show(
            owner,
            message ?? "TUN-режим перехватывает трафик всех программ и требует прав администратора.\n\n" +
                       "Перезапустить VieNLink с повышением прав? Списки и настройки сохранятся.",
            "VieNLink",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        if (TryRestartElevated(argument))
        {
            _exitRequested = true;
            Close();
            return;
        }

        MessageBox.Show(
            owner,
            "Не удалось запросить права. Запустите VieNLink.exe от имени администратора вручную.",
            "VieNLink",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowPlacement();

        if (!_exitRequested && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray?.ShowBalloon("VieNLink свёрнут", "Приложение осталось в трее рядом с часами.");
            }

            return;
        }

        _closing = true;
        _autoTimer?.Stop();
        _counterTimer?.Stop();
        _mtprotoCollectCts?.Cancel();
        _mtprotoCheckCts?.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        AppLog.LineWritten -= OnLogLine;
        _idleTimer?.Stop();
        _idleCts?.Cancel();
        _mtprotoCollectCts?.Cancel();
        _mtprotoCheckCts?.Cancel();
        _probe.StopNow();
        _zapretWidget?.Close();
        _tray?.Dispose();
        // Синхронно: прокси, ядро и обход DPI должны быть сняты до выхода из приложения.
        _tunBypass.StopNow();
        _desync.Dispose();
        _zapret.Stop();
        _manager.ShutdownSync();
        base.OnClosed(e);
    }
}
