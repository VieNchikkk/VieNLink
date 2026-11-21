using System.Runtime.InteropServices;
using Android.App;

namespace VieNLink.Mobile;

/// <summary>
/// Точка входа Android-версии: подставляет платформенные реализации в общее ядро
/// и перенаправляет stderr в файл — Go-ядро пишет туда текст паники, иначе он теряется.
/// </summary>
[Application(Label = "VieNLink")]
public sealed class VieNLinkApp : Application
{
    private const int O_WRONLY = 0x1;
    private const int O_CREAT = 0x40;
    private const int O_APPEND = 0x400;
    private const int StderrFileDescriptor = 2;

    public VieNLinkApp(IntPtr handle, Android.Runtime.JniHandleOwnership transfer) : base(handle, transfer)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();

        RedirectStandardError();
        RegisterSecretProtector();
        WarmNetworkAssemblies();
        ProbeScheduler.Start(this);
        MtProtoScheduler.Start(this);
        if (!ProbeScheduler.IsProbeProcess())
        {
            Maintenance.EnsureScheduled(this);
        }
    }

    /// <summary>
    /// Прогрев сетевых сборок на главном потоке. Их ленивая загрузка из фоновых потоков
    /// (health-check автопереключения, NetworkProbe, Maintenance, :probe) гоняется в
    /// libmonodroid (EmbeddedAssemblies::open_from_bundles) и роняет процесс SIGSEGV на
    /// .NET TP Worker — это и есть краши после автопереключения сервера. Один раз трогаем
    /// типы здесь, на главном потоке: дальше сборки уже загружены, и гонки нет.
    /// </summary>
    private static void WarmNetworkAssemblies()
    {
        try
        {
            _ = typeof(System.Net.Dns);
            _ = typeof(System.Net.IPAddress);
            _ = typeof(System.Net.Sockets.TcpClient);
            _ = typeof(System.Net.Sockets.UdpClient);
            _ = typeof(System.Net.Http.HttpClient);
            _ = typeof(System.Net.Http.SocketsHttpHandler);
            _ = typeof(System.Net.Security.SslStream);
        }
        catch (Exception exception)
        {
            Android.Util.Log.Warn("VieNLink", "не удалось прогреть сетевые сборки: " + exception.Message);
        }
    }

    /// <summary>
    /// Go-рантайм печатает панику в stderr, а на Android stderr процесса — /dev/null.
    /// Подменяем fd 2 файлом: после падения текст паники можно прочитать через
    /// `adb shell run-as com.vienlink.app cat files/core-stderr.log`.
    /// </summary>
    private void RedirectStandardError()
    {
        try
        {
            var path = Path.Combine(FilesDir!.AbsolutePath, "core-stderr.log");
            var fd = open(path, O_WRONLY | O_CREAT | O_APPEND, 0x1A4); // 0644
            if (fd >= 0)
            {
                dup2(fd, StderrFileDescriptor);
            }
        }
        catch (Exception exception)
        {
            Android.Util.Log.Warn("VieNLink", "не удалось перенаправить stderr: " + exception.Message);
        }
    }

    private void RegisterSecretProtector()
    {
        try
        {
            SecretProtector.Current = new KeystoreSecretStore();
        }
        catch (Java.Lang.Exception exception)
        {
            // Без шифрования источники писать нельзя: файл с токенами остался бы открытым.
            AppLog.Write("android", "Keystore недоступен — источники подписок не сохранятся", exception);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);
}
