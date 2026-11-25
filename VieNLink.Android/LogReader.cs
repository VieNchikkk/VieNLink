using System.Text;

namespace VieNLink.Mobile;

/// <summary>Чтение хвоста журнала приложения и stderr ядра с фильтром по категории.</summary>
internal static class LogReader
{
    public static (string Text, string Source) Read(
        bool core,
        int maxLines = 400,
        string category = AppLogCategories.All)
    {
        if (core)
        {
            var path = System.IO.Path.Combine(
                Android.App.Application.Context.FilesDir?.AbsolutePath ?? AppPaths.DataDirectory,
                "core-stderr.log");
            return (Tail(path, maxLines, "Лог ядра пуст: ядро ещё не писало в stderr."),
                "Ядро: " + path + " · без категорий");
        }

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
        var matched = all.Where(line => AppLogCategories.Matches(line, category)).ToList();
        var start = Math.Max(0, matched.Count - maxLines);
        var tail = matched[start..];
        var empty = category == AppLogCategories.All
            ? "Журнал пуст."
            : $"В категории «{category}» пока пусто. Откройте «{AppLogCategories.All}», чтобы увидеть весь журнал.";
        var text = tail.Count == 0 ? empty : string.Join(System.Environment.NewLine, tail);
        var source = $"Приложение: {latest} · {category} · строк {tail.Count} из {all.Length}";
        return (text, source);
    }

    private static string Tail(string path, int count, string empty)
    {
        if (!File.Exists(path))
        {
            return empty;
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
        {
            return empty;
        }

        var start = Math.Max(0, lines.Length - count);
        return string.Join(System.Environment.NewLine, lines[start..]);
    }

    /// <summary>Очищает сегодняшний журнал приложения, не трогая архив за прошлые дни.</summary>
    public static bool Clear()
    {
        var directory = AppPaths.LogsDirectory;
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var today = System.IO.Path.Combine(directory, $"vienlink-{DateTime.Now:yyyyMMdd}.log");
        try
        {
            if (File.Exists(today))
            {
                File.WriteAllText(today, string.Empty, new UTF8Encoding(false));
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
