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
    // ---------- настройки ----------

    private async void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        var portsChanged = _settings.MixedPort.ToString() != PortBox.Text.Trim() || _settings.ApiPort.ToString() != ApiPortBox.Text.Trim();
        _settings.MixedPort = ParseInt(PortBox, _settings.MixedPort, 1024, 65500);
        _settings.ApiPort = ParseInt(ApiPortBox, _settings.ApiPort, 1024, 65500);
        _settings.MaxNodesInConfig = ParseInt(MaxNodesBox, _settings.MaxNodesInConfig, 20, 5000);
        _settings.MaxTotalNodes = ParseInt(MaxTotalNodesBox, _settings.MaxTotalNodes, 1000, 100000);
        _settings.MaxDelayMs = ParseInt(MaxDelayBox, _settings.MaxDelayMs, 100, 20000);
        _settings.TestUrl = string.IsNullOrWhiteSpace(TestUrlBox.Text) ? "https://www.youtube.com/" : TestUrlBox.Text.Trim();
        _settings.ProxyBypass = BypassBox.Text.Trim();
        _settings.DefaultSni = SniBox.Text.Trim();
        _settings.TlsFragment = TlsFragmentCheck.IsChecked == true;
        _settings.TlsRecordFragment = TlsRecordFragmentCheck.IsChecked == true;
        _settings.ApplySystemProxy = ApplyProxyCheck.IsChecked == true;
        _settings.AutoSelectBest = AutoBestCheck.IsChecked == true;
        _settings.AutoFailover = AutoFailoverCheck.IsChecked == true;
        _settings.ConnectOnlyVerified = OnlyVerifiedConnectCheck.IsChecked == true;
        _settings.ConnectOnlyMine = OnlyMineConnectCheck.IsChecked == true;
        _settings.ZapretAutoStart = ZapretAutoStartCheck.IsChecked == true;
        _settings.DesyncAutoStart = DesyncAutoStartCheck.IsChecked == true;
        _settings.ZapretBlockQuic = ZapretBlockQuicCheck.IsChecked == true;
        _settings.DesyncBlockQuic = DesyncBlockQuicCheck.IsChecked == true;
        _settings.TunBypassBlockQuic = TunBypassBlockQuicCheck.IsChecked == true;
        _settings.TunBypassAutoStart = TunBypassAutoStartCheck.IsChecked == true;
        _settings.TestAfterRefresh = TestAfterRefreshCheck.IsChecked == true;
        _settings.IdleTestEnabled = IdleTestCheck.IsChecked == true;
        _settings.IdleTestMinutes = ParseInt(IdleMinutesBox, _settings.IdleTestMinutes, 1, 120);
        _settings.IdleTestBatch = ParseInt(IdleBatchBox, _settings.IdleTestBatch, 5, 500);
        _settings.IdleTestIntervalMinutes = ParseInt(IdleIntervalBox, _settings.IdleTestIntervalMinutes, 1, 1440);
        _settings.IdleRecheckHours = ParseInt(IdleRecheckBox, _settings.IdleRecheckHours, 1, 168);
        _settings.MinimizeToTray = MinimizeTrayCheck.IsChecked == true;
        _settings.WidgetTopmost = WidgetTopmostCheck.IsChecked == true;
        _settings.KillSwitch = KillSwitchCheck.IsChecked == true;
        _settings.ShowTraffic = ShowTrafficCheck.IsChecked == true;
        _settings.ShowExitInfo = ShowExitInfoCheck.IsChecked == true;
        _settings.RunAtStartup = AutostartCheck.IsChecked == true;
        _settings.AutoUpdateSubscriptions = AutoUpdateCheck.IsChecked == true;
        _settings.AutoUpdateHours = ParseInt(AutoUpdateHoursBox, _settings.AutoUpdateHours, 1, 168);
        _settings.KeepUnseenNodeDays = ParseInt(KeepUnseenDaysBox, _settings.KeepUnseenNodeDays, 1, 365);
        _settings.AutoRefreshMinutes = ParseInt(AutoRefreshBox, _settings.AutoRefreshMinutes, 0, 1440);
        _settings.SourceFreshnessHours = ParseInt(SourceFreshnessHoursBox, _settings.SourceFreshnessHours, 1, 24 * 30);
        _settings.AutoCollectServers = AutoCollectServersCheck.IsChecked == true &&
                                      _settings.AutoRefreshMinutes > 0;
        _settings.AutoDiscoverSources = AutoDiscoverCheck.IsChecked == true;
        _settings.AutoDiscoverHours = ParseInt(AutoDiscoverHoursBox, _settings.AutoDiscoverHours, 1, 168);
        _settings.AutoMtProto = AutoMtProtoCheck.IsChecked == true;
        _settings.AutoUpdateCore = AutoCoreUpdateCheck.IsChecked == true;
        _settings.AutoCheckAppUpdates = AutoCheckAppUpdatesCheck.IsChecked == true;
        ApplyRoutingSettingsFromUi();

        LoadSettingsToUi();
        SaveSettings();
        _zapretWidget?.RefreshTopmostState();
        UpdateHeaderState();
        FooterText.Text = "Настройки сохранены";

        // Уменьшили лимит — сразу подрезаем список и кэш, не дожидаясь обновления.
        if (NodeCount > _settings.MaxTotalNodes)
        {
            ReplaceNodes(AllRows.Select(r => r.Node).ToList());
            var kept = AllRows.Select(r => r.Node).ToList();
            await Task.Run(() => NodeCacheWriter.Save(kept, force: true));
            SetStatus("Лимит серверов уменьшен", $"В списке {kept.Count} из {_settings.MaxTotalNodes}");
        }

        if (portsChanged && _manager.IsConnected)
        {
            MessageBox.Show(
                this,
                "Порты изменены. Отключитесь и подключитесь заново, чтобы ядро заработало на новых портах.",
                "VieNLink",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        await Task.CompletedTask;
    }

    private static int ParseInt(TextBox box, int fallback, int minimum, int maximum) =>
        int.TryParse(box.Text.Trim(), out var value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private void SaveSettings() => JsonFileStore.Write(AppPaths.SettingsPath, _settings);

    private void OnAutomationToggleClick(object sender, RoutedEventArgs e)
    {
        _settings.AutomationPaused = !_settings.AutomationPaused;
        SaveSettings();
        ApplyAutomationState();
        UpdateAutomationUi();
    }

    /// <summary>Останавливает или запускает всю фоновую автоматику, не трогая туннель.</summary>
    private void ApplyAutomationState()
    {
        if (_settings.AutomationPaused)
        {
            _idleTimer?.Stop();
            _autoTimer?.Stop();
            _idleCts?.Cancel();
            _mtprotoCollectCts?.Cancel();
            _mtprotoCheckCts?.Cancel();
            _probe.StopNow();
            AppendLog("Все процессы остановлены: проверка серверов, MTProto, обслуживание и автообновления выключены");
            SetStatus("Все процессы остановлены", "Туннель и ручные действия продолжают работать");
            return;
        }

        _lastIdleTestUtc = null;
        _lastRecheckUtc = null;
        _lastMtProtoCollectUtc = null;
        _idleTimer?.Start();
        _autoTimer?.Start();
        AppendLog("Все процессы запущены: проверка серверов, MTProto и автообновления снова работают");
        SetStatus("Все процессы запущены", "Фоновая проверка серверов и MTProto включены");
    }

    private void UpdateAutomationUi()
    {
        if (AutomationToggleButton is null)
        {
            return;
        }

        if (_settings.AutomationPaused)
        {
            AutomationToggleButton.Content = "Запустить все процессы";
            AutomationToggleButton.Style = (Style)FindResource("PrimaryButton");
            AutomationToggleButton.ToolTip = "Снова включить все фоновые процессы приложения";
            if (AutomationHintText is not null)
            {
                AutomationHintText.Text = "Все фоновые процессы остановлены. Туннель и ручные действия продолжают работать.";
            }
        }
        else
        {
            AutomationToggleButton.Content = "Остановить все процессы";
            AutomationToggleButton.Style = (Style)FindResource("WarningButton");
            AutomationToggleButton.ToolTip = "Полностью выключить фоновые процессы приложения";
            if (AutomationHintText is not null)
            {
                AutomationHintText.Text = "Останавливает все фоновые процессы приложения: проверку серверов, MTProto, " +
                                          "обслуживание и автообновления. Подключённый туннель и ручные действия продолжают работать.";
            }
        }
    }

    /// <summary>Галочка «Виджет поверх всех окон» применяется сразу, без «Сохранить».</summary>
    private void OnWidgetTopmostChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.WidgetTopmost = WidgetTopmostCheck.IsChecked == true;
        SaveSettings();
        _zapretWidget?.RefreshTopmostState();
    }

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        _settings.Theme = LightThemeCheck.IsChecked == true ? "Light" : "Dark";
        App.ApplyTheme(_settings.Theme);
        WindowChromeTheme.Apply(this);
        SaveSettings();
    }

    private void OnOpenDataClick(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.DataDirectory);

}
