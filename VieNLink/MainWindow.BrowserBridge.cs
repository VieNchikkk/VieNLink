namespace VieNLink;

/// <summary>
/// Мостик между встроенным браузером и VPN-частью главного окна: состояние подключения,
/// подключение/отключение, смена сервера и обходы DPI. Браузер не дублирует логику —
/// он вызывает те же методы, что кнопки главного окна.
/// </summary>
public partial class MainWindow
{
    internal event Action? BrowserVpnStateChanged;

    internal bool BrowserVpnConnected => _manager.IsConnected;

    internal bool BrowserVpnHeld => _manager.IsKillSwitchHeld;

    internal string BrowserVpnServer => _manager.CurrentNode?.Name ?? string.Empty;

    internal int BrowserVpnDelay => _manager.CurrentNode?.DelayMs ?? 0;

    internal bool BrowserVpnBusy => _busy;

    internal bool BrowserVpnZapret => _zapret.IsRunning;

    internal bool BrowserVpnDesync => _desync.IsRunning;

    internal string BrowserVpnMode => ActiveModeName;

    /// <summary>Подключение из браузера: лучший проверенный сервер, без выбора сервиса.</summary>
    internal async Task<bool> BrowserConnectAsync()
    {
        if (_manager.IsConnected)
        {
            return true;
        }

        if (_busy)
        {
            return false;
        }

        var node = PickBestNode();
        if (node is null)
        {
            SetStatus("Нет серверов", "Проверенных серверов нет — откройте VieNLink, обновите списки и нажмите «Все серверы»");
            ShowFromTray();
            return false;
        }

        if (!BeginOperation("подключение из браузера"))
        {
            return false;
        }

        try
        {
            await ConnectAsync(node, automatic: true);
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Подключение из браузера не удалось", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }

        return _manager.IsConnected;
    }

    /// <summary>Отключение из браузера: снимает сервер и все обходы, как кнопка «Отключить».</summary>
    internal async Task BrowserDisconnectAsync()
    {
        await DisconnectEverythingAsync();
        UpdateConnectionUi();
    }

    /// <summary>Смена сервера из браузера: следующий по приоритету, как кнопка «Сменить сервер».</summary>
    internal async Task<bool> BrowserSwitchServerAsync()
    {
        if (!_manager.IsConnected)
        {
            return await BrowserConnectAsync();
        }

        await SwitchToNextNodeAsync();
        UpdateConnectionUi();
        return _manager.IsConnected;
    }

    internal Task BrowserToggleZapretAsync() => WidgetToggleZapretAsync();

    internal Task BrowserToggleDesyncAsync() => WidgetToggleDesyncAsync();
}
