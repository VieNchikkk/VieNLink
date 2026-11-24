using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VieNLink;

/// <summary>
/// Локальный канал для отдельного приложения «VieNLink Браузер»: состояние VPN и команды.
/// Браузер — своя установка, поэтому говорит с VPN только через этот канал.
/// </summary>
internal static class VpnPipeServer
{
    private const string PipeName = "VieNLink.Vpn";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    private static CancellationTokenSource? _server;

    public static void Start(MainWindow window)
    {
        if (_server is not null)
        {
            return;
        }

        _server = new CancellationTokenSource();
        _ = Task.Run(() => ListenAsync(window, _server.Token));
    }

    private static async Task ListenAsync(MainWindow window, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);
                _ = HandleAsync(window, server);
                server = null;
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                return;
            }
            catch (Exception exception)
            {
                AppLog.Write("pipe", "Ошибка локального канала браузера", exception);
                server?.Dispose();
                await Task.Delay(500, token);
            }
        }
    }

    private static async Task HandleAsync(MainWindow window, NamedPipeServerStream server)
    {
        try
        {
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
            };

            var line = await reader.ReadLineAsync();
            var command = ParseCommand(line);
            await ExecuteAsync(window, command);
            await writer.WriteLineAsync(JsonSerializer.Serialize(Describe(window), JsonOptions));
        }
        catch (Exception exception)
        {
            AppLog.Write("pipe", "Не удалось обработать запрос браузера", exception);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static string ParseCommand(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "state";
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("cmd", out var command)
                ? command.GetString() ?? "state"
                : "state";
        }
        catch (JsonException)
        {
            return "state";
        }
    }

    private static async Task ExecuteAsync(MainWindow window, string command)
    {
        switch (command)
        {
            case "connect":
                await RunOnUiAsync(window, window.BrowserConnectAsync);
                break;
            case "disconnect":
                await RunOnUiAsync(window, window.BrowserDisconnectAsync);
                break;
            case "switch":
                await RunOnUiAsync(window, window.BrowserSwitchServerAsync);
                break;
            case "zapret":
                await RunOnUiAsync(window, window.BrowserToggleZapretAsync);
                break;
            case "desync":
                await RunOnUiAsync(window, window.BrowserToggleDesyncAsync);
                break;
            case "open":
                await window.Dispatcher.InvokeAsync(window.ShowFromTray);
                break;
        }
    }

    private static async Task RunOnUiAsync(MainWindow window, Func<Task> action)
    {
        try
        {
            var task = await window.Dispatcher.InvokeAsync(action);
            await task;
        }
        catch (Exception exception)
        {
            AppLog.Write("pipe", "Команда браузера не выполнена", exception);
        }
    }

    private static async Task RunOnUiAsync<T>(MainWindow window, Func<Task<T>> action)
    {
        try
        {
            var task = await window.Dispatcher.InvokeAsync(action);
            await task;
        }
        catch (Exception exception)
        {
            AppLog.Write("pipe", "Команда браузера не выполнена", exception);
        }
    }

    private static VpnDescription Describe(MainWindow window) => new(
        true,
        window.BrowserVpnConnected,
        window.BrowserVpnHeld,
        window.BrowserVpnBusy,
        window.BrowserVpnServer,
        window.BrowserVpnDelay,
        window.BrowserVpnZapret,
        window.BrowserVpnDesync,
        string.IsNullOrWhiteSpace(window.BrowserVpnMode) ? "Отключено" : window.BrowserVpnMode);

    private sealed record VpnDescription(
        bool Available,
        bool Connected,
        bool Held,
        bool Busy,
        string Server,
        int Delay,
        bool Zapret,
        bool Desync,
        string Mode);
}
