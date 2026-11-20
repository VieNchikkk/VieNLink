using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VieNLink;

/// <summary>
/// Системный прокси Windows (WinINET): этого достаточно, чтобы YouTube открылся в браузере.
/// Перед изменением сохраняем прежние настройки и возвращаем их при отключении.
/// </summary>
internal static class SystemProxy
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public static SystemProxyBackup Capture()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        if (key is null)
        {
            return new SystemProxyBackup();
        }

        return new SystemProxyBackup
        {
            Captured = true,
            ProxyEnable = key.GetValue("ProxyEnable") is int enabled ? enabled : 0,
            ProxyServer = key.GetValue("ProxyServer") as string,
            ProxyOverride = key.GetValue("ProxyOverride") as string,
            AutoConfigUrl = key.GetValue("AutoConfigURL") as string,
        };
    }

    public static void Apply(int port, SystemProxyBackup backup, string? bypass = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);

        // Запоминаем чужие настройки на диск: если приложение упадут, при следующем запуске
        // они будут восстановлены, а не останутся указывать на мёртвый порт.
        backup.AppliedServer = $"127.0.0.1:{port}";
        JsonFileStore.Write(AppPaths.ProxyBackupPath, backup);

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        // Простая запись host:port понимается и WinINET, и .NET; схема = всё через один mixed-порт.
        key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);

        // <local> оставляем всегда, остальное — из настроек; так служебные адреса не идут через узел.
        var rules = string.IsNullOrWhiteSpace(bypass) ? "<local>" : "<local>;" + bypass.Trim();
        key.SetValue("ProxyOverride", rules, RegistryValueKind.String);

        // PAC имеет приоритет над ручным прокси, поэтому на время работы его убираем.
        if (key.GetValue("AutoConfigURL") is not null)
        {
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        }

        Notify();
        AppLog.Write("proxy", $"Системный прокси включён: 127.0.0.1:{port}");
    }

    public static void Restore(SystemProxyBackup backup)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (!backup.Captured)
        {
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
            Notify();
            TryDeleteBackup();
            return;
        }

        key.SetValue("ProxyEnable", backup.ProxyEnable, RegistryValueKind.DWord);
        WriteOrDelete(key, "ProxyServer", backup.ProxyServer);
        WriteOrDelete(key, "ProxyOverride", backup.ProxyOverride);
        WriteOrDelete(key, "AutoConfigURL", backup.AutoConfigUrl);
        Notify();
        TryDeleteBackup();
        AppLog.Write("proxy", "Системный прокси возвращён в исходное состояние");
    }

    /// <summary>
    /// Если прошлый запуск завершился аварийно, в реестре мог остаться наш прокси на мёртвый порт.
    /// Проверяем файл резервной копии и возвращаем настройки.
    /// </summary>
    public static bool RecoverIfNeeded()
    {
        var backup = JsonFileStore.Read<SystemProxyBackup>(AppPaths.ProxyBackupPath);
        if (backup?.AppliedServer is null)
        {
            return false;
        }

        var portText = backup.AppliedServer.Split(':').LastOrDefault();
        if (!int.TryParse(portText, out var port))
        {
            TryDeleteBackup();
            return false;
        }

        // Порт ещё слушается — значит, работает другой экземпляр; ничего не трогаем.
        if (IsListening(port))
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        var current = key?.GetValue("ProxyServer") as string;
        if (!string.Equals(current, backup.AppliedServer, StringComparison.OrdinalIgnoreCase))
        {
            // Пользователь уже настроил прокси сам — не вмешиваемся.
            TryDeleteBackup();
            return false;
        }

        Restore(backup);
        AppLog.Write("proxy", "Восстановлен системный прокси после аварийного завершения");
        return true;
    }

    private static bool IsListening(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(TimeSpan.FromMilliseconds(400)) && client.Connected;
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or AggregateException)
        {
            return false;
        }
    }

    private static void TryDeleteBackup()
    {
        try
        {
            if (File.Exists(AppPaths.ProxyBackupPath))
            {
                File.Delete(AppPaths.ProxyBackupPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("proxy", "Не удалось удалить файл резервной копии прокси", exception);
        }
    }

    private static void WriteOrDelete(RegistryKey key, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value, RegistryValueKind.String);
        }
    }

    private static void Notify()
    {
        _ = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        _ = InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }
}
