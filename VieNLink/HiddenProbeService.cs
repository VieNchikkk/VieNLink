using System.IO;

namespace VieNLink;

/// <summary>
/// Скрытая проверка узлов: отдельный процесс sing-box на своих локальных портах,
/// без TUN и без системного прокси. Он не трогает текущее подключение пользователя,
/// поэтому проверять узлы можно и во время работы, и когда туннель уже поднят.
/// </summary>
internal sealed class HiddenProbeService : IAsyncDisposable
{
    private readonly SingBoxRunner _runner = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _running;

    public bool IsRunning => _running;

    /// <summary>
    /// Проверяет узлы через скрытое ядро. Возвращает итог; параллельный вызов
    /// не запускается — возвращается пустой итог.
    /// </summary>
    public async Task<TestSummary> TestAsync(
        IReadOnlyList<ProxyNode> nodes,
        AppSettings settings,
        IProgress<string>? progress,
        Action<ProxyNode>? onNodeTested,
        CancellationToken cancellationToken,
        int concurrency = 3,
        int timeoutMs = 3000,
        bool waitForGate = true)
    {
        if (nodes.Count == 0)
        {
            return new TestSummary(0, 0, 0, 0);
        }

        // Ручная проверка ждёт окончания фоновой; фоновая при занятом ядре
        // просто пропускает такт и не копит очередь.
        if (waitForGate)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new TestSummary(0, 0, 0, 0);
        }

        _running = true;
        var configPath = AppPaths.ProbeConfigPath;
        try
        {
            if (!File.Exists(AppPaths.SingBoxPath))
            {
                AppLog.Write("probe", "Скрытая проверка пропущена: ядро sing-box не скачано");
                return new TestSummary(0, 0, 0, 0);
            }

            var ordered = nodes
                .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (ordered.Count == 0)
            {
                return new TestSummary(0, 0, 0, 0);
            }

            // Копия настроек: проверка не должна менять режим подключения пользователя.
            var probeSettings = settings.Clone();
            probeSettings.TunnelMode = TunnelMode.SystemProxy;
            probeSettings.SiteRouteMode = SiteRouteMode.All;
            probeSettings.OnlySelectedApps = false;

            var mixedPort = ProxyManager.FindFreePort(Math.Max(settings.MixedPort, 22000));
            var apiPort = ProxyManager.FindFreePort(Math.Max(settings.ApiPort, mixedPort + 1));
            var secret = Guid.NewGuid().ToString("N");

            var json = SingBoxConfigBuilder.Build(
                ordered,
                ordered[0].Tag,
                mixedPort,
                apiPort,
                secret,
                probeSettings);
            SingBoxConfigBuilder.WriteToFile(configPath, json);
            await SingBoxRunner.CheckConfigAsync(configPath, cancellationToken).ConfigureAwait(false);

            AppLog.Write("probe", $"Скрытая проверка: узлов {ordered.Count}, mixed {mixedPort}, API {apiPort}");
            await _runner.StartAsync(configPath, apiPort, TimeSpan.FromSeconds(20), cancellationToken)
                .ConfigureAwait(false);

            using var api = new ClashApiClient(apiPort, secret);
            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (await api.PingAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }

            return await ProxyManager.TestWithApiAsync(
                api,
                ordered,
                progress,
                onNodeTested,
                cancellationToken,
                concurrency,
                timeoutMs,
                youtubeUrl: null,
                telegramUrl: null).ConfigureAwait(false);
        }
        finally
        {
            await _runner.StopAsync().ConfigureAwait(false);
            try
            {
                File.Delete(configPath);
            }
            catch (IOException)
            {
                // Следующая проверка перезапишет конфиг.
            }

            _running = false;
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _runner.StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>Немедленная остановка скрытого ядра при выходе из приложения.</summary>
    public void StopNow() => _runner.StopNow();
}
