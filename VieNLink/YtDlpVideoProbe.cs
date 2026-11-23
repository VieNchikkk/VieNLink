using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VieNLink;

/// <summary>
/// Проверка видео YouTube через yt-dlp: он сам получает подписанный адрес потока
/// (n-sig/PO-токен) и качает видео своим HTTP-клиентом. curl и .NET на этом провайдере
/// получают 0 байт, а yt-dlp с клиентом android — реальные байты. yt-dlp скачивается
/// один раз, как WinDivert/sing-box.
/// </summary>
internal static class YtDlpVideoProbe
{
    private const long MinBytes = 64 * 1024;
    private const long MinFileSize = 1_000_000;

    private static readonly string[] DownloadUrls =
    {
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe",
        "https://ghfast.top/https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe",
        "https://gh-proxy.com/https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    internal static string Path => System.IO.Path.Combine(AppPaths.DataDirectory, "yt-dlp.exe");

    internal static bool IsInstalled =>
        File.Exists(Path) && new FileInfo(Path).Length > MinFileSize;

    /// <summary>Скачивает yt-dlp один раз. Возвращает false, если все зеркала недоступны.</summary>
    internal static async Task<bool> EnsureAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (IsInstalled)
        {
            return true;
        }

        Directory.CreateDirectory(AppPaths.DataDirectory);
        var temp = Path + ".download";
        Exception? lastError = null;
        foreach (var url in DownloadUrls)
        {
            try
            {
                progress?.Report("Скачиваю пробник видео (yt-dlp)…");
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var target = File.Create(temp))
                {
                    await source.CopyToAsync(target, cancellationToken);
                }

                if (new FileInfo(temp).Length <= MinFileSize)
                {
                    throw new InvalidOperationException("yt-dlp скачался не полностью");
                }

                File.Move(temp, Path, overwrite: true);
                AppLog.Write("desync", "Пробник видео yt-dlp готов: " + Path);
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
                AppLog.Write("desync", $"yt-dlp не скачался с {url}", exception);
            }
        }

        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        catch (IOException)
        {
        }

        AppLog.Write("desync", "yt-dlp недоступен: " + lastError?.Message);
        return false;
    }

    /// <summary>Скачивает короткий ролик; возвращает реально полученные байты (0 — не вышло).</summary>
    internal static async Task<long> MeasureAsync(CancellationToken cancellationToken)
    {
        if (!IsInstalled)
        {
            return 0;
        }

        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vienlink-ytdlp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var startInfo = new ProcessStartInfo(Path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
                     {
                         "--no-warnings", "--no-playlist", "--no-progress",
                         "--socket-timeout", "15", "--retries", "1",
                         "--extractor-args", "youtube:player_client=android",
                         "-f", "worst[height<=360]/worst",
                         "--max-filesize", "6M",
                         "-o", System.IO.Path.Combine(directory, "video.%(ext)s"),
                         "https://www.youtube.com/watch?v=jNQXAC9IVRw",
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return 0;
            }

            // stdout/stderr читаем параллельно, иначе при большом выводе процесс зависнет.
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            var files = Directory.GetFiles(directory);
            var bytes = files.Length == 0
                ? 0
                : files.Max(file => new FileInfo(file).Length);
            if (bytes < MinBytes)
            {
                var text = (await error).Trim();
                AppLog.Write("desync",
                    $"yt-dlp не скачал видео: {bytes} байт" +
                    (text.Length == 0 ? string.Empty : " — " + text[..Math.Min(300, text.Length)]));
                return 0;
            }

            Trace?.Invoke($"yt-dlp: видео {bytes} байт");
            return bytes;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.Write("desync", "Проверка видео через yt-dlp не удалась", exception);
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal static Action<string>? Trace { get; set; }
}
