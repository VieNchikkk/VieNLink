namespace VieNLink.Mobile;

/// <summary>
/// Один раз при запуске приложения, после стандартной стартовой перепроверки:
/// проверяет отсев через отдельный процесс :probe (без VpnService, без переключения
/// текущего сервера) и возвращает открывшиеся серверы в рабочие. Если обычный VPN
/// уже подключён — не запускается, чтобы ничего не трогать.
/// </summary>
internal static class RejectedBootCheckService
{
    private static int _done;
    private static int _running;

    public static bool IsRunning => Volatile.Read(ref _running) == 1;

    public static async Task RunAsync(Android.Content.Context context)
    {
        if (Interlocked.CompareExchange(ref _done, 1, 0) != 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var settings = SettingsStore.Current;
            var android = AndroidSettingsStore.Current;
            if (settings.AutomationPaused || !settings.IdleTestEnabled || !android.AutoProbe)
            {
                return;
            }

            if (TunnelService.IsBypassMode ||
                TunnelService.State == TunnelState.Running ||
                CoreRuntime.IsRunning ||
                RejectedNodeStore.Load().Count == 0)
            {
                return;
            }

            // Тот же путь, что и кнопка «Перепроверить всё», только автоматически:
            // отдельный :probe, по одному, DNS через сервер. Текущий VPN не трогается.
            var result = await RejectedCheckService
                .RecheckAsync(null, CancellationToken.None)
                .ConfigureAwait(false);
            AppLog.Write("nodes", result.Error ??
                $"Автоперепроверка отсева при запуске: проверено {result.Tested}, возвращено {result.Restored}");
        }
        catch (Exception exception)
        {
            AppLog.Write("nodes", "Автоперепроверка отсева при запуске не удалась", exception);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
