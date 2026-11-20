using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace VieNLink;

/// <summary>
/// TUN-обход без прокси-узла и без WinDivert: sing-box поднимает TUN и отправляет
/// TCP в локальный SOCKS5-форвардер, который открывает прямой канал и режет TLS.
/// Нужны права администратора и wintun.dll.
/// </summary>
internal sealed class TunBypassService : IAsyncDisposable
{
    private readonly TlsFragmentForwarder _forwarder = new();
    private readonly SingBoxRunner _runner = new();
    private int _apiPort;
    private string _apiSecret = string.Empty;
    private bool _running;

    public bool IsRunning => _running;

    public int ForwarderPort => _forwarder.Port;

    public long AcceptedConnections => _forwarder.AcceptedConnections;

    public long FragmentedConnections => _forwarder.FragmentedConnections;

    public StreamFragmentMode Mode { get; private set; }

    public TunBypassService()
    {
        _runner.LogLine += line => AppLog.Write("tun-bypass-core", line);
    }

    public async Task StartAsync(
        StreamFragmentMode mode,
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        string? onlyProcess = null)
    {
        await StopAsync();
        await SingBoxCore.EnsureAsync(progress, cancellationToken);
        await SingBoxCore.EnsureWintunAsync(progress, cancellationToken);

        await _forwarder.StartAsync(mode, 0);
        try
        {
            _apiPort = FindFreePort(20950);
            _apiSecret = Guid.NewGuid().ToString("N");
            var processName = Path.GetFileName(Environment.ProcessPath) ?? "VieNLink.exe";
            var json = SingBoxConfigBuilder.BuildTunBypass(
                _forwarder.Port, _apiPort, _apiSecret, settings.TunBypassBlockQuic, processName, onlyProcess);
            SingBoxConfigBuilder.WriteToFile(AppPaths.TunBypassConfigPath, json);
            progress?.Report("Проверка конфига TUN-обхода…");
            await SingBoxRunner.CheckConfigAsync(AppPaths.TunBypassConfigPath, cancellationToken);

            progress?.Report($"Запуск TUN-обхода: форвардер 127.0.0.1:{_forwarder.Port}…");
            await _runner.StartAsync(
                AppPaths.TunBypassConfigPath, _apiPort, TimeSpan.FromSeconds(30), cancellationToken);
            _running = true;
            Mode = mode;
            AppLog.Write("tun-bypass",
                $"TUN-обход запущен: режим {mode}, форвардер {_forwarder.Port}, API {_apiPort}");
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        _running = false;
        try
        {
            await _runner.StopAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            AppLog.Write("tun-bypass", "Не удалось остановить ядро TUN-обхода", exception);
        }

        await _forwarder.StopAsync();
    }

    /// <summary>Немедленная остановка при выходе: процесс ядра снимается без ожидания.</summary>
    public void StopNow()
    {
        _running = false;
        _runner.StopNow();
        _ = _forwarder.StopAsync();
    }

    private static readonly string[] ProbeUrls =
    {
        "https://www.youtube.com/generate_204",
        "https://t.me/",
    };

    /// <summary>
    /// Проверка через TUN отдельным процессом curl.exe: запросы самого VieNLink.exe
    /// намеренно исключены из TUN (иначе форвардер замкнулся бы сам на себя).
    /// </summary>
    public static async Task<DesyncProbeResult> ProbeAsync(
        CancellationToken cancellationToken,
        int index = 0,
        string name = "TUN-обход")
    {
        var ok = 0;
        var youtube = false;
        var telegram = false;
        var times = new List<int>();
        foreach (var url in ProbeUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var startInfo = new ProcessStartInfo("curl.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var argument in new[]
                         {
                             "-4", "-s", "-o", "NUL", "-w", "%{http_code}",
                             "--max-time", "8", "--noproxy", "*", "--http1.1", url,
                         })
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    continue;
                }

                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                var code = int.TryParse(output.Trim(), out var value) ? value : 0;
                var success = code is >= 200 and < 400;
                if (success)
                {
                    ok++;
                    times.Add((int)stopwatch.ElapsedMilliseconds);
                }

                if (url.Contains("youtube", StringComparison.OrdinalIgnoreCase))
                {
                    youtube = success;
                }
                else
                {
                    telegram = success;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is Win32Exception or OperationCanceledException)
            {
                AppLog.Write("tun-bypass", "Проверка curl.exe не удалась", exception);
            }
        }

        var average = times.Count == 0 ? 0 : (int)times.Average();
        return new DesyncProbeResult(index, name, ok, ProbeUrls.Length, average, youtube, telegram);
    }

    /// <summary>
    /// Автоподключение с подбором стратегии: по очереди запускает TUN-обход с каждой
    /// стратегией фрагментации и проверяет доступность сайтов через отдельный процесс
    /// curl.exe. Возвращает итоги по всем стратегиям; выбирать лучшую должен вызывающий.
    /// </summary>
    public async Task<IReadOnlyList<DesyncProbeResult>> AutoTuneAsync(
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        string? onlyProcess = null)
    {
        var results = new List<DesyncProbeResult>();
        var strategies = TlsFragmentForwarder.Strategies;
        for (var index = 0; index < strategies.Count; index++)
        {
            var strategy = strategies[index];
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Стратегия {index + 1}/{strategies.Count}: «{strategy.Name}»…");
            try
            {
                await StartAsync(strategy.Mode, settings, progress, cancellationToken, onlyProcess);
                // Небольшая пауза, чтобы TUN-адаптер и маршруты полностью поднялись.
                await Task.Delay(400, cancellationToken);
                var result = await ProbeAsync(cancellationToken, index, strategy.Name);
                results.Add(result);
                AppLog.Write("tun-bypass",
                    $"Автоподключение: {strategy.Name} — {result.Ok}/{result.Total} " +
                    $"(YouTube {(result.YoutubeOk ? "да" : "нет")}, " +
                    $"Telegram {(result.TelegramOk ? "да" : "нет")}), {result.AverageMs} мс");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                AppLog.Write("tun-bypass", $"Стратегия «{strategy.Name}» не запустилась", exception);
                results.Add(new DesyncProbeResult(index, strategy.Name, 0, ProbeUrls.Length, 0, false, false));
            }
            finally
            {
                await StopAsync();
            }

            // Пауза между стратегиями, чтобы виртуальный адаптер успел сняться.
            await Task.Delay(300, cancellationToken);
        }

        return results;
    }

    private static int FindFreePort(int preferred)    {
        for (var port = preferred; port < preferred + 100; port++)
        {
            try
            {
                using var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException)
            {
                // Порт занят — пробуем следующий.
            }
        }

        return preferred;
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
