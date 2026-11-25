using System.IO;
using System.Text.Json;

namespace VieNLink;

/// <summary>
/// Атомарная запись JSON (временный файл + .bak) и чтение с восстановлением из резервной копии.
/// Отдельные методы шифруют файл через DPAPI — для данных с токенами подписок.
/// </summary>
internal static class JsonFileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Компактная запись для больших кэшей: без отступов файл в разы меньше и быстрее.</summary>
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Файлы пишутся из фона (кэш серверов), поэтому общий временный файл защищаем.</summary>
    private static readonly object WriteLock = new();

    public static T? Read<T>(string path) where T : class => Read<T>(path, Options);

    /// <summary>
    /// Чтение файла с чужими именами полей: настройки сериализации задаёт вызывающий
    /// (например, база параметров обхода ведётся вручную в репозитории строчными именами).
    /// </summary>
    public static T? Read<T>(string path, JsonSerializerOptions options) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length == 0)
                {
                    continue;
                }

                // Читаем потоком: копия JSON в строку или byte[] на 20 МБ кэша не нужна.
                SkipUtf8Bom(stream);
                return JsonSerializer.Deserialize<T>(stream, options);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                AppLog.Write("json", $"Не удалось прочитать {Path.GetFileName(candidate)}", exception);
            }
        }

        return null;
    }

    public static void Write<T>(string path, T value) => WriteCore(path, value, Options);

    /// <summary>Запись с настройками сериализации вызывающего (парно к <see cref="Read{T}(string,JsonSerializerOptions)"/>).</summary>
    public static void Write<T>(string path, T value, JsonSerializerOptions options) =>
        WriteCore(path, value, options);

    /// <summary>Компактная запись (без отступов) — для кэша серверов на тысячи записей.</summary>
    public static void WriteCompact<T>(string path, T value) => WriteCore(path, value, CompactOptions);

    private static void WriteCore<T>(string path, T value, JsonSerializerOptions options)
    {
        lock (WriteLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    // Сериализация сразу в поток: без промежуточной строки на десятки мегабайт.
                    JsonSerializer.Serialize(stream, value, options);
                }

                if (File.Exists(path))
                {
                    File.Copy(path, path + ".bak", overwrite: true);
                }

                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("json", $"Не удалось сохранить {Path.GetFileName(path)}", exception);
            }
        }
    }

    /// <summary>Пишет JSON, зашифрованный платформенным ключом (DPAPI/Keystore): файл не читается без ключа.</summary>
    public static bool WriteProtected<T>(string path, T value)
    {
        lock (WriteLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var protector = SecretProtector.Current
                                ?? throw new InvalidOperationException("Шифрование секретов недоступно");
                var json = JsonSerializer.Serialize(value, Options);
                var bytes = protector.Protect(json);
                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(path))
                {
                    File.Copy(path, path + ".bak", overwrite: true);
                }

                File.Move(temporary, path, overwrite: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                 or InvalidOperationException)
            {
                AppLog.Write("json", $"Не удалось сохранить {Path.GetFileName(path)}", exception);
                return false;
            }
        }
    }

    /// <summary>Читает файл, записанный <see cref="WriteProtected{T}"/>; null — нет файла или ключ не подошёл.</summary>
    public static T? ReadProtected<T>(string path) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(candidate);
                if (bytes.Length == 0)
                {
                    continue;
                }

                var protector = SecretProtector.Current;
                if (protector is null)
                {
                    AppLog.Write("json", "Шифрование секретов недоступно: пропускаю " + Path.GetFileName(candidate));
                    continue;
                }

                var json = protector.Unprotect(bytes);
                if (json is null)
                {
                    continue;
                }

                return JsonSerializer.Deserialize<T>(json, Options);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                AppLog.Write("json", $"Не удалось прочитать {Path.GetFileName(candidate)}", exception);
            }
        }

        return null;
    }

    /// <summary>Пропускает UTF-8 BOM, который старые версии могли записать в файл.</summary>
    private static void SkipUtf8Bom(FileStream stream)
    {
        Span<byte> bom = stackalloc byte[3];
        var read = stream.Read(bom);
        var hasBom = read == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF;
        if (!hasBom)
        {
            stream.Position = 0;
        }
    }
}
