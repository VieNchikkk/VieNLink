using System.IO;
using System.Text;

namespace VieNLink;

/// <summary>
/// Простой файловый журнал: события источников, запуск ядра, переключение прокси и ошибки.
/// </summary>
internal static class AppLog
{
    private const long MaximumLogBytes = 2L * 1024 * 1024;
    private const int RetentionDays = 14;
    private static readonly object Gate = new();
    private static bool _initialized;

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
                DeleteOldLogs();
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
            }
            catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
            {
                // Если диск недоступен, молча продолжаем.
            }
        }
    }

    private static string GetCurrentLogPath() =>
        Path.Combine(AppPaths.LogsDirectory, $"vienlink-{DateTime.Now:yyyyMMdd}.log");

    private static void DeleteOldLogs()
    {
        var threshold = DateTime.Now.AddDays(-RetentionDays);
        // Основные журналы и их ротированные копии (.log.1).
        foreach (var pattern in new[] { "vienlink-*.log", "vienlink-*.log.1" })
        {
            foreach (var path in Directory.EnumerateFiles(AppPaths.LogsDirectory, pattern))
            {
                try
                {
                    if (File.GetLastWriteTime(path) < threshold)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Старый журнал удалится позже.
                }
            }
        }
    }
}
