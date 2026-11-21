namespace VieNLink.Mobile;

/// <summary>
/// Обработчик событий ядра. Ядро обращается к нему при перезагрузке службы, отладке и
/// запросе системного прокси. Пустой обработчик нельзя передавать как null: Go-ядро
/// вызывает методы напрямую и падает, если их нет.
/// </summary>
internal sealed class LibBoxServerHandler : Java.Lang.Object, Libbox.ICommandServerHandler
{
    public Libbox.SystemProxyStatus? SystemProxyStatus => null;

    /// <summary>SSH-агент не поддерживаем: отрицательное значение означает отказ.</summary>
    public int ConnectSSHAgent() => -1;

    public void ServiceReload()
    {
        Android.Util.Log.Info("VieNLink", "ядро: ServiceReload");
    }

    public void ServiceStop()
    {
        Android.Util.Log.Info("VieNLink", "ядро: ServiceStop");
    }

    public void SetSystemProxyEnabled(bool enabled)
    {
    }

    public void TriggerNativeCrash()
    {
    }

    /// <summary>Отладочные сообщения ядра — в logcat, иначе их не увидеть.</summary>
    public void WriteDebugMessage(string? message)
    {
        if (!string.IsNullOrEmpty(message))
        {
            Android.Util.Log.Debug("VieNLink", message);
        }
    }
}
