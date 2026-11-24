#if JAVA_PLATFORM
using Java.Lang.Reflect;

namespace VieNLink.Mobile;

/// <summary>
/// Мост C# → Java-ядро: только старт/остановка и регистрация VpnService.
/// Всё, что ядро вызывает на каждом соединении, остаётся в Java-классах
/// (<c>com.vienlink.platform.VieNLinkJavaBridge</c>).
/// </summary>
internal static class JavaBridge
{
    private const string BridgeClass = "com.vienlink.platform.VieNLinkJavaBridge";

    private static Java.Lang.Class? _class;
    private static Method? _setService;
    private static Method? _start;
    private static Method? _stop;
    private static Method? _closeTun;
    private static Method? _hasTun;

    private static void Ensure()
    {
        if (_class is not null)
        {
            return;
        }

        // Class.forName(String) берёт boot class loader: Java-классы приложения
        // оттуда не видны. Нужен загрузчик приложения (PathClassLoader).
        var loader = Android.App.Application.Context.ClassLoader
                     ?? Java.Lang.Thread.CurrentThread()?.ContextClassLoader;
        var bridge = Java.Lang.Class.ForName(BridgeClass, false, loader)
                     ?? throw new InvalidOperationException("JavaBridge: класс не найден");
        _setService = bridge.GetMethod("setService", Java.Lang.Class.FromType(typeof(Android.Net.VpnService)));
        _start = bridge.GetMethod("start", Java.Lang.Class.FromType(typeof(Java.Lang.String)));
        _stop = bridge.GetMethod("stop");
        _closeTun = bridge.GetMethod("closeTun");
        _hasTun = bridge.GetMethod("hasTun");
        _class = bridge;
    }

    public static void SetService(Android.Net.VpnService service)
    {
        Ensure();
        _setService!.Invoke(null, service);
    }

    public static void Start(string config)
    {
        Ensure();
        _start!.Invoke(null, new Java.Lang.String(config));
    }

    public static void Stop()
    {
        Ensure();
        _stop!.Invoke(null);
    }

    public static void CloseTun()
    {
        Ensure();
        _closeTun!.Invoke(null);
    }

    public static bool HasTun()
    {
        Ensure();
        var value = _hasTun!.Invoke(null);
        return string.Equals(value?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
    }
}
#endif
