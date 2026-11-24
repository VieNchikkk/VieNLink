using System.IO;

namespace VieNLink;

/// <summary>
/// Пути приложения: всё пользовательское живёт в %LOCALAPPDATA%\VieNLink,
/// ядро sing-box — в подпапке core, чтобы его не потерять при обновлении exe.
/// При первом запуске данные прежних названий (DomVPN, MyrrProxy) переносятся автоматически.
/// </summary>
internal static class AppPaths
{
    private const string AppFolderName = "VieNLink";

    /// <summary>Прежние названия папки данных: DomVPN (1.0.x) и MyrrProxy (самое первое).</summary>
    private static readonly string[] LegacyFolderNames = { "DomVPN", "MyrrProxy" };

    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string SourcesPath { get; } = Path.Combine(DataDirectory, "sources.json");

    /// <summary>Зашифрованный DPAPI список источников: ссылки подписок содержат токены.</summary>
    public static string SourcesProtectedPath { get; } = Path.Combine(DataDirectory, "sources.dat");
    public static string NodesPath { get; } = Path.Combine(DataDirectory, "nodes.json");

    /// <summary>
    /// Проверенные рабочие серверы отдельно от кучи непроверенных: файл маленький,
    /// его можно переписывать после каждой проверки, не трогая кэш на тысячи записей.
    /// </summary>
    public static string WorkingNodesPath { get; } = Path.Combine(DataDirectory, "nodes-working.json");

    public static string SettingsPath { get; } = Path.Combine(DataDirectory, "settings.json");

    /// <summary>Настройки встроенного браузера: поиск, домашняя страница, закладки, вкладки.</summary>
    public static string BrowserSettingsPath { get; } = Path.Combine(DataDirectory, "browser.json");

    /// <summary>История посещений встроенного браузера.</summary>
    public static string BrowserHistoryPath { get; } = Path.Combine(DataDirectory, "browser-history.json");

    /// <summary>Рабочие серверы, переставшие работать: храним отдельно для ручной перепроверки.</summary>
    public static string RejectedNodesPath { get; } = Path.Combine(DataDirectory, "nodes-rejected.json");
    public static string LogsDirectory { get; } = Path.Combine(DataDirectory, "logs");
    public static string CoreDirectory { get; } = Path.Combine(DataDirectory, "core");
    public static string RuntimeDirectory { get; } = Path.Combine(DataDirectory, "runtime");
    public static string DesyncDirectory { get; } = Path.Combine(DataDirectory, "desync");

    public static string SingBoxPath { get; } = Path.Combine(CoreDirectory, "sing-box.exe");
    public static string ActiveConfigPath { get; } = Path.Combine(RuntimeDirectory, "sing-box-config.json");
    public static string TunBypassConfigPath { get; } = Path.Combine(RuntimeDirectory, "tun-bypass-config.json");

    /// <summary>Конфиг скрытого пробного ядра: проверка серверов не мешает основному подключению.</summary>
    public static string ProbeConfigPath { get; } = Path.Combine(RuntimeDirectory, "probe-config.json");
    public static string MtProtoPath { get; } = Path.Combine(DataDirectory, "mtproto.json");
    public static string ProxyBackupPath { get; } = Path.Combine(DataDirectory, "proxy-backup.json");
    public static string ZapretHistoryPath { get; } = Path.Combine(DataDirectory, "zapret-history.json");

    /// <summary>Удачные конфиги обходов, отсортированные по качеству подключения.</summary>
    public static string BypassProfilesPath { get; } = Path.Combine(DataDirectory, "bypass-profiles.json");

    /// <summary>Кэш дат публикации GitHub-источников: чтобы после перезапуска не жечь квоту API заново.</summary>
    public static string FreshnessPath { get; } = Path.Combine(DataDirectory, "freshness.json");

    /// <summary>Кэш сырых ответов источников для условных GET (ETag → 304 не меняет список).</summary>
    public static string RawCacheDirectory { get; } = Path.Combine(DataDirectory, "rawcache");

    /// <summary>ETag ответов источников: ключ — URL источника.</summary>
    public static string RawEtagsPath { get; } = Path.Combine(DataDirectory, "raw-etags.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CoreDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(DesyncDirectory);
        Directory.CreateDirectory(RawCacheDirectory);
    }

    private static string ResolveDataDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var preferred = Path.Combine(local, AppFolderName);
        if (Directory.Exists(preferred))
        {
            return preferred;
        }

        // Первый запуск VieNLink: переносим настройки, кэш, логи и скачанное ядро
        // из папки прежнего названия, чтобы пользователь не настраивал всё заново.
        foreach (var legacyName in LegacyFolderNames)
        {
            var legacy = Path.Combine(local, legacyName);
            if (!Directory.Exists(legacy))
            {
                continue;
            }

            try
            {
                Directory.Move(legacy, preferred);
                return preferred;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Старая папка занята запущенной прежней версией — работаем в ней до следующего запуска.
                return legacy;
            }
        }

        return preferred;
    }
}
