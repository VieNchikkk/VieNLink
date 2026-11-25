using System.IO;
using System.Text;

namespace VieNLink;

/// <summary>
/// Простой файловый журнал: события источников, запуск ядра, переключение прокси и ошибки.
/// Хранится один суточный файл с префиксом категории; старые файлы удаляются через
/// <see cref="RetentionHours"/> часов — журнал нужен для разбора свежих сбоев, а не как архив.
/// </summary>
internal static class AppLog
{
    private const long MaximumLogBytes = 2L * 1024 * 1024;

    /// <summary>Сколько часов хранить журналы и служебные файлы журнала.</summary>
    public const int RetentionHours = 72;

    private static readonly object Gate = new();
    private static bool _initialized;
    private static DateTime _lastCleanupUtc = DateTime.MinValue;

    /// <summary>Отключает запись в файл для self-test, чтобы не засорять рабочий журнал.</summary>
    public static bool SuppressFile { get; set; }

    public static event Action<string>? LineWritten;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            try
            {
                Directory.CreateDirectory(AppPaths.LogsDirectory);
                DeleteOldFiles();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Журнал не должен мешать запуску.
            }
        }
    }

    public static void Write(string category, string message, Exception? exception = null)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("HH:mm:ss.fff"))
            .Append("  ")
            .Append(category)
            .Append("  ")
            .Append(message.ReplaceLineEndings(" · "));

        if (exception is not null)
        {
            // Для разбора падений у пользователя нужен стек, а не только текст ошибки.
            line.Append(" | ").Append(exception.ToString().ReplaceLineEndings(" ⏎ "));
        }

        var text = line.ToString();
        LineWritten?.Invoke(text);

        if (SuppressFile)
        {
            return;
        }

        Initialize();

        lock (Gate)
        {
            try
            {
                var path = GetCurrentLogPath();
                if (File.Exists(path) && new FileInfo(path).Length > MaximumLogBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                File.AppendAllText(path, text.ReplaceLineEndings(" ") + Environment.NewLine, Encoding.UTF8);

                // При долгой работе без перезапуска чистим старые файлы не реже раза в час.
                if (DateTime.UtcNow - _lastCleanupUtc > TimeSpan.FromHours(1))
                {
                    _lastCleanupUtc = DateTime.UtcNow;
                    DeleteOldFiles();
                }
            }
            catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
            {
                // Если диск недоступен, молча продолжаем.
            }
        }
    }

    /// <summary>Хвост сегодняшнего (или последнего) журнала с фильтром по категории.</summary>
    public static (string Text, string Source) ReadTail(int maxLines = 500, string? category = null)
    {
        Initialize();
        var directory = AppPaths.LogsDirectory;
        var latest = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "vienlink-*.log*")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (latest is null)
        {
            return ("Журнал приложения пуст.", "Файл ещё не создан");
        }

        var all = File.ReadAllLines(latest);
        var filter = string.IsNullOrWhiteSpace(category) ? AppLogCategories.All : category;
        var matched = all.Where(line => AppLogCategories.Matches(line, filter)).ToList();
        var start = Math.Max(0, matched.Count - Math.Max(1, maxLines));
        var tail = matched[start..];
        var empty = filter == AppLogCategories.All
            ? "Журнал пуст."
            : $"В категории «{filter}» пока пусто. Откройте «{AppLogCategories.All}», чтобы увидеть весь журнал.";
        return (tail.Count == 0 ? empty : string.Join(Environment.NewLine, tail),
            $"Приложение: {latest} · {filter} · строк {tail.Count} из {all.Length}");
    }

    /// <summary>Очищает сегодняшний журнал, не трогая архив прошлых суток.</summary>
    public static bool ClearToday()
    {
        try
        {
            var today = GetCurrentLogPath();
            if (!File.Exists(today))
            {
                return true;
            }

            File.WriteAllText(today, string.Empty, new UTF8Encoding(false));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Чистка «ненужной информации»: журналы, ротированные копии, stderr ядра и старые
    /// временные конфиги. Пользовательские данные (источники, серверы, история) не трогаем.
    /// </summary>
    public static void DeleteOldFiles(bool includeRuntime = false)
    {
        var threshold = DateTime.Now.AddHours(-RetentionHours);
        var logs = AppPaths.LogsDirectory;
        if (Directory.Exists(logs))
        {
            foreach (var path in Directory.EnumerateFiles(logs, "*", SearchOption.TopDirectoryOnly))
            {
                DeleteIfOld(path, threshold);
            }
        }

        // Кэш сырых ответов источников: после 72 часов он устарел, а при 304 приложение
        // само перезапросит список (см. RawContentStore). Это сотни килобайт на диск.
        if (Directory.Exists(AppPaths.RawCacheDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(AppPaths.RawCacheDirectory, "*",
                         SearchOption.TopDirectoryOnly))
            {
                DeleteIfOld(path, threshold);
            }
        }

        // stderr ядра лежит в каталоге данных, а не в logs.
        DeleteIfOld(Path.Combine(AppPaths.DataDirectory, "core-stderr.log"), threshold);

        // Недописанные временные конфиги ядра: в них бывают UUID и ключи.
        // Только при старте: работающий туннель может держать конфиг дольше 72 часов.
        if (includeRuntime && Directory.Exists(AppPaths.RuntimeDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(AppPaths.RuntimeDirectory, "*.json",
                         SearchOption.TopDirectoryOnly))
            {
                DeleteIfOld(path, threshold);
            }
        }
    }

    /// <summary>Удалить конкретный служебный файл, если он старше срока хранения.</summary>
    public static void CleanupFile(string path) =>
        DeleteIfOld(path, DateTime.Now.AddHours(-RetentionHours));

    private static void DeleteIfOld(string path, DateTime threshold)
    {
        try
        {
            if (!File.Exists(path) || File.GetLastWriteTime(path) >= threshold)
            {
                return;
            }

            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Файл занят — удалится при следующей чистке.
        }
    }

    private static string GetCurrentLogPath() =>
        Path.Combine(AppPaths.LogsDirectory, $"vienlink-{DateTime.Now:yyyyMMdd}.log");
}
