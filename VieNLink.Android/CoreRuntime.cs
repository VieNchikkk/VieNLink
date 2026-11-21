using Android.Content;
using System.Text.Json;

namespace VieNLink.Mobile;

/// <summary>
/// Единая точка работы с libbox внутри процесса: <c>Setup</c> ровно один раз,
/// один <c>CommandServer</c> на приложение, запуск и остановка конфига.
///
/// Одновременно в процессе не может жить два ядра, поэтому VPN-служба и проверка узлов
/// ходят через этот класс: проверка запускает конфиг без TUN и останавливает его,
/// а если туннель уже поднят — только опрашивает Clash API работающего ядра.
/// </summary>
internal static class CoreRuntime
{
    /// <summary>Порты основного процесса (см. docs/plan-2026.md §0).</summary>
    public static int MixedPort { get; private set; } = 20800;

    public static int ApiPort { get; private set; } = 20801;

    public static string ApiSecret { get; private set; } = "vienlink";

    /// <summary>Процесс :probe — отдельное ядро со своими портами и папкой кэша.</summary>
    public static bool IsProbeProcess { get; private set; }

    private static string? _basePath;

    private static readonly object Gate = new();
    private static Libbox.CommandServer? _server;
    private static LibBoxPlatform? _platform;
    private static LibBoxServerHandler? _handler;
    private static bool _setupDone;

    /// <summary>
    /// Настройка процесса проверки: свои порты (21800/21801) и отдельная папка,
    /// чтобы два libbox не делили cache-file и configuration.json.
    /// </summary>
    public static void ConfigureProbe(Context context)
    {
        lock (Gate)
        {
            if (IsProbeProcess)
            {
                return;
            }

            IsProbeProcess = true;
            MixedPort = FindFreePort(21800);
            ApiPort = FindFreePort(MixedPort + 1);
            ApiSecret = "vienlink-probe";
            var root = Path.Combine(context.FilesDir?.AbsolutePath ?? context.ApplicationInfo!.DataDir!, "probe");
            Directory.CreateDirectory(root);
            _basePath = root;
            Android.Util.Log.Info("VieNLink", $"probe core: mixed {MixedPort}, API {ApiPort}, {root}");
        }
    }

    private static int FindFreePort(int preferred)
    {
        for (var port = preferred; port < preferred + 20; port++)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Порт занят — пробуем следующий.
            }
        }

        return preferred;
    }

    public static bool IsRunning
    {
        get
        {
            lock (Gate)
            {
                return _server is not null;
            }
        }
    }

    /// <summary>Теги узлов, реально принятых ядром в последнем конфиге.</summary>
    public static IReadOnlyCollection<string> LoadedTags { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Обязательная инициализация libbox: без неё Go-ядро падает при создании CommandServer.
    /// FixAndroidStack нужен из-за особенностей стека Android; пути должны существовать.
    /// </summary>
    public static void EnsureSetup(Context context)
    {
        lock (Gate)
        {
            if (_setupDone)
            {
                return;
            }

            var basePath = _basePath ?? context.FilesDir?.AbsolutePath ?? context.ApplicationInfo!.DataDir!;
            var tempPath = IsProbeProcess
                ? Path.Combine(context.CacheDir?.AbsolutePath ?? basePath, "probe")
                : context.CacheDir?.AbsolutePath ?? basePath;
            Directory.CreateDirectory(tempPath);
            var options = new Libbox.SetupOptions
            {
                BasePath = basePath,
                WorkingPath = basePath,
                TempPath = tempPath,
                FixAndroidStack = true,
                CommandServerListenPort = 0,
                Debug = true,
            };

            Libbox.Libbox.Setup(options);
            _setupDone = true;
            Android.Util.Log.Info("VieNLink", "libbox setup: " + basePath);
        }
    }

    /// <summary>
    /// Запускает (или перезагружает) конфиг на одном сервере процесса.
    /// Повторный вызов не создаёт второе ядро: иначе libbox не делит cache-файл
    /// и StartOrReloadService падает с «initialize cache-file: timeout».
    /// </summary>
    public static bool Start(string configJson)
    {
        lock (Gate)
        {
            if (_server is null)
            {
                _platform ??= new LibBoxPlatform();
                _handler ??= new LibBoxServerHandler();
                _server = Libbox.Libbox.NewCommandServer(_handler, _platform);
                Android.Util.Log.Info("VieNLink", "сервер создан=" + (_server is not null));
                if (_server is null)
                {
                    return false;
                }
            }

            // null здесь нельзя: Go-ядро разыменовывает настройки переопределения.
            var overrides = new Libbox.OverrideOptions();
            _server.StartOrReloadService(configJson, overrides);
            LoadedTags = ReadTags(configJson);
            return true;
        }
    }

    /// <summary>Останавливает службу и закрывает сервер. Повторный вызов безопасен.</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            try
            {
                _server?.CloseService();
                _server?.Close();
            }
            catch (Exception exception)
            {
                // Закрытие ядра не должно ронять приложение.
                Android.Util.Log.Warn("VieNLink", "остановка ядра: " + exception.Message);
            }

            _server = null;
            LoadedTags = Array.Empty<string>();
        }
    }

    /// <summary>Теги outbound'ов конфига: по ним видно, какие узлы ядро реально приняло.</summary>
    private static IReadOnlyCollection<string> ReadTags(string configJson)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (!document.RootElement.TryGetProperty("outbounds", out var outbounds) ||
                outbounds.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            foreach (var outbound in outbounds.EnumerateArray())
            {
                if (outbound.TryGetProperty("tag", out var tag) &&
                    tag.ValueKind == JsonValueKind.String)
                {
                    var value = tag.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        tags.Add(value);
                    }
                }
            }
        }
        catch (JsonException exception)
        {
            Android.Util.Log.Warn("VieNLink", "не удалось прочитать теги конфига: " + exception.Message);
        }

        return tags;
    }
}
