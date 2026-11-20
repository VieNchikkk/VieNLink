using System.IO;
using Microsoft.Win32;

namespace VieNLink;

/// <summary>
/// Автозапуск при входе в Windows. Ключ HKCU\...\Run не требует прав администратора
/// и виден в «Диспетчере задач → Автозагрузка», в отличие от службы или планировщика.
/// </summary>
internal static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VieNLink";

    /// <summary>Приложение уже прописано в автозапуск.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception exception) when (exception is System.Security.SecurityException
                                                 or UnauthorizedAccessException
                                                 or IOException)
            {
                AppLog.Write("app", "Не удалось прочитать автозапуск", exception);
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Не удалось открыть ключ автозапуска Windows");

        if (enabled)
        {
            // --minimized: стартуем в трее, не выкидывая окно поверх работы пользователя.
            var (file, arguments) = LaunchCommand();
            key.SetValue(ValueName, "\"" + file + "\" " + arguments, RegistryValueKind.String);
            AppLog.Write("app", "Автозапуск включён: " + file);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            AppLog.Write("app", "Автозапуск выключен");
        }
    }

    /// <summary>
    /// Убирает автозапуск прежнего названия и переносит его на текущий exe:
    /// иначе после переименования Windows пыталась бы запускать старый путь.
    /// </summary>
    public static void MigrateLegacy()
    {
        const string oldValueName = "DomVPN";
        try
        {
            var migrate = false;
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            {
                if (key?.GetValue(oldValueName) is string old && old.Length > 0)
                {
                    key.DeleteValue(oldValueName, throwOnMissingValue: false);
                    // Переносим только если автозапуск не был выключен в новой версии.
                    migrate = key.GetValue(ValueName) is null;
                }
            }

            if (migrate)
            {
                SetEnabled(true);
                AppLog.Write("app", "Автозапуск перенесён со старого названия на VieNLink");
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
                                             or UnauthorizedAccessException
                                             or IOException
                                             or InvalidOperationException)
        {
            AppLog.Write("app", "Не удалось перенести автозапуск", exception);
        }
    }

    /// <summary>
    /// Команда запуска. Для релиза это сам exe; при запуске через dotnet — dotnet + DLL,
    /// иначе автозапуск открыл бы пустой dotnet без приложения.
    /// </summary>
    private static (string File, string Arguments) LaunchCommand()
    {
        var processPath = Environment.ProcessPath ?? string.Empty;
        if (processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(assembly))
            {
                return (processPath, "\"" + assembly + "\" --minimized");
            }
        }

        if (processPath.Length > 0)
        {
            return (processPath, "--minimized");
        }

        return (Path.Combine(AppContext.BaseDirectory, "VieNLink.exe"), "--minimized");
    }
}
