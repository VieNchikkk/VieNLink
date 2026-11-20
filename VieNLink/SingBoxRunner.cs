using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace VieNLink;

/// <summary>
/// Запуск и остановка процесса sing-box. stderr ядра идёт в журнал приложения,
/// последние строки сохраняются для сообщения об ошибке.
/// </summary>
internal sealed class SingBoxRunner : IAsyncDisposable
{
    private readonly StringBuilder _lastErrors = new();
    private Process? _process;

    public bool IsRunning
    {
        get
        {
            try
            {
                return _process is { HasExited: false };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public event Action<string>? LogLine;

    public static async Task CheckConfigAsync(string configPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(SingBoxCore.ExecutablePath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("check");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configPath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить sing-box.exe");

        // Читаем оба потока, иначе буфер одного из них может заполниться и заблокировать процесс.
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        _ = await outputTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Проверка конфига sing-box не прошла: " + LastLines(error, 4));
        }
    }

    public async Task StartAsync(string configPath, int apiPort, TimeSpan readyTimeout, CancellationToken cancellationToken)
    {
        await StopAsync();

        var startInfo = new ProcessStartInfo(SingBoxCore.ExecutablePath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = AppPaths.RuntimeDirectory,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configPath);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить sing-box.exe");
        _process = process;

        // Ядро живёт в job-объекте: при закрытии приложения (даже аварийном) sing-box будет убит системой.
        KillOnCloseJob.Assign(process);

        process.OutputDataReceived += (_, args) => Forward(args.Data);
        process.ErrorDataReceived += (_, args) => Forward(args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow + readyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("sing-box завершился при запуске: " + LastLines(_lastErrors.ToString(), 5));
            }

            if (await CanConnectAsync(apiPort, cancellationToken))
            {
                AppLog.Write("core", $"sing-box запущен (pid {process.Id})");
                return;
            }

            await Task.Delay(150, cancellationToken);
        }

        await StopAsync();
        throw new TimeoutException("sing-box не открыл локальный порт за " + readyTimeout.TotalSeconds + " с: " + LastLines(_lastErrors.ToString(), 5));
    }

    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("core", "Не удалось корректно остановить sing-box", exception);
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>Немедленная остановка без ожидания — только для выхода из приложения.</summary>
    public void StopNow()
    {
        var process = _process;
        _process = null;
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("core", "Не удалось остановить sing-box при выходе", exception);
        }
    }

    private void Forward(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_lastErrors)
        {
            _lastErrors.AppendLine(line);
            if (_lastErrors.Length > 8000)
            {
                _lastErrors.Remove(0, _lastErrors.Length - 6000);
            }
        }

        LogLine?.Invoke(line);
    }

    private static async Task<bool> CanConnectAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string LastLines(string text, int count)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(count));
    }

    /// <summary>
    /// Job-объект Windows с флагом KILL_ON_JOB_CLOSE: дочерний процесс не переживёт закрытие
    /// дескриптора, то есть выход или падение приложения.
    /// </summary>
    internal static class KillOnCloseJob
    {
        private const int ExtendedLimitInformationClass = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private static readonly IntPtr Handle = Create();

        public static void Assign(Process process)
        {
            if (Handle == IntPtr.Zero)
            {
                return;
            }

            try
            {
                _ = AssignProcessToJobObject(Handle, process.Handle);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                AppLog.Write("core", "Не удалось привязать процесс к job-объекту", exception);
            }
        }

        private static IntPtr Create()
        {
            try
            {
                var handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                var information = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose,
                    },
                };

                if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, ref information, Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                {
                    return IntPtr.Zero;
                }

                return handle;
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                AppLog.Write("core", "Job-объекты недоступны", exception);
                return IntPtr.Zero;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}

