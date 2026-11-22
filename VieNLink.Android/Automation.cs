namespace VieNLink.Mobile;

/// <summary>
/// Глобальный выключатель фоновой автоматики: проверка серверов, MTProto,
/// обслуживание по будильнику, автообновления и автопереключение.
/// Туннель и ручные действия продолжают работать.
/// </summary>
internal static class Automation
{
    public static bool Paused => SettingsStore.Current.AutomationPaused;

    public static void Apply(Android.Content.Context context)
    {
        var paused = SettingsStore.Current.AutomationPaused;
        try
        {
            if (paused)
            {
                ProbeClient.Cancel();
                ProbeScheduler.Stop();
                MtProtoScheduler.Stop();
                Maintenance.Cancel(context);
            }
            else
            {
                ProbeClient.Resume();
                ProbeScheduler.Start(context);
                MtProtoScheduler.Start(context);
                Maintenance.EnsureScheduled(context);
            }

            AppLog.Write("automation", paused ? "Все процессы остановлены" : "Все процессы запущены");
        }
        catch (Exception exception)
        {
            AppLog.Write("automation", "Не удалось применить состояние автоматики", exception);
        }
    }
}
