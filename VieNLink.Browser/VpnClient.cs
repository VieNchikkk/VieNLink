using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VieNLink;

/// <summary>Состояние VPN, которое приходит от VieNLink.exe по локальному каналу.</summary>
internal sealed class VpnState
{
    public bool Available { get; set; }

    public bool Connected { get; set; }

    public bool Held { get; set; }

    public bool Busy { get; set; }

    public string Server { get; set; } = string.Empty;

    public int Delay { get; set; }

    public bool Zapret { get; set; }

    public bool Desync { get; set; }

    public string Mode { get; set; } = "Отключено";
}

/// <summary>
/// Клиент локального канала к VieNLink.exe. Браузер — отдельная установка,
/// поэтому не вызывает VPN напрямую, а спрашивает состояние и команды по named pipe.
/// Без запущенного VPN панель просто показывает «VPN не запущен».
/// </summary>
internal static class VpnClient
{
    private const string PipeName = "VieNLink.Vpn";

    public static event EventHandler? StateChanged;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly object Sync = new();
    private static VpnState _state = new() { Mode = "Отключено" };
    private static CancellationTokenSource? _refresh;

    public static VpnState State
    {
        get
        {
            lock (Sync)
            {
                return _state;
            }
        }
    }

    public static bool Available => State.Available;

    public static bool Connected => State.Connected;

    public static bool Held => State.Held;

    public static bool Busy => State.Busy;

    public static int DelayMs => State.Delay;

    public static bool ZapretRunning => State.Zapret;

    public static bool DesyncRunning => State.Desync;

    public static string ActiveMode => State.Mode;

    public static string StatusLabel
    {
        get
        {
            if (!Available)
            {
                return "VPN не запущен";
            }

            if (Connected)
            {
                var name = string.IsNullOrWhiteSpace(State.Server) ? "сервер" : State.Server;
                return DelayMs > 0 ? $"{name} · {DelayMs} мс" : name;
            }

            return Held ? "Трафик заблокирован (kill switch)" : "не подключён";
        }
    }

    public static void Start()
    {
        if (_refresh is not null)
        {
            return;
        }

        _refresh = new CancellationTokenSource();
        _ = Task.Run(() => PollAsync(_refresh.Token));
    }

    private static async Task PollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Apply(await QueryAsync("state", token) ?? new VpnState { Mode = "Отключено" });
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static void Apply(VpnState state)
    {
        bool changed;
        lock (Sync)
        {
            changed = _state.Available != state.Available ||
                      _state.Connected != state.Connected ||
                      _state.Held != state.Held ||
                      _state.Busy != state.Busy ||
                      _state.Server != state.Server ||
                      _state.Delay != state.Delay ||
                      _state.Zapret != state.Zapret ||
                      _state.Desync != state.Desync ||
                      _state.Mode != state.Mode;
            _state = state;
        }

        // Событие — только при реальном изменении: иначе панель и страница дёргаются каждые 2 с.
        if (changed)
        {
            StateChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static async Task<bool> ConnectAsync() => await SendAsync("connect");

    public static async Task<bool> DisconnectAsync() => await SendAsync("disconnect");

    public static async Task<bool> SwitchServerAsync() => await SendAsync("switch");

    public static async Task<bool> ToggleZapretAsync() => await SendAsync("zapret");

    public static async Task<bool> ToggleDesyncAsync() => await SendAsync("desync");

    public static void OpenMain() => _ = QueryAsync("open", CancellationToken.None);

    /// <summary>Разово обновить состояние — например, перед проверкой, установлен ли VPN.</summary>
    public static async Task RefreshAsync() =>
        Apply(await QueryAsync("state", CancellationToken.None) ?? new VpnState { Mode = "Отключено" });

    private static async Task<bool> SendAsync(string command)
    {
        var state = await QueryAsync(command, CancellationToken.None);
        if (state is null)
        {
            return false;
        }

        Apply(state);
        return true;
    }

    private static async Task<VpnState?> QueryAsync(string command, CancellationToken token)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(800, timeout.Token);

            var request = JsonSerializer.Serialize(new { cmd = command });
            var bytes = Encoding.UTF8.GetBytes(request + "\n");
            await client.WriteAsync(bytes, timeout.Token);
            await client.FlushAsync(timeout.Token);

            using var reader = new StreamReader(client, Encoding.UTF8);
            var line = await reader.ReadLineAsync(timeout.Token);
            return string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize<VpnState>(line, JsonOptions);
        }
        catch (Exception)
        {
            // Канал недоступен — это нормально, если VPN не запущен.
            return null;
        }
    }
}
