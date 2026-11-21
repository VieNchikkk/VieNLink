using Android.App;
using Android.Content;

namespace VieNLink.Mobile;

/// <summary>
/// Автоматическая проверка обновлений Android-версии на GitHub.
/// При запуске приложение само проверяет релизы (не чаще раза в 6 часов),
/// скачивает APK и предлагает установить; в фоне раз в сутки показывает
/// уведомление. Установку подтверждает пользователь — этого требует Android.
/// </summary>
internal static class UpdateCenter
{
    private const string PreferencesName = "vienlink-update";
    private const string KeyLastCheck = "last-check-utc";
    private const string KeySkippedTag = "skipped-tag";
    private const string KeySkippedAt = "skipped-at-utc";

    private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan SkipInterval = TimeSpan.FromHours(12);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int _launchCheckStarted;

    /// <summary>Проверка при открытии главного экрана. force — из уведомления или adb.</summary>
    public static void StartLaunchCheck(Activity activity, bool force = false)
    {
        if (!force && Interlocked.CompareExchange(ref _launchCheckStarted, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var release = await CheckAsync(activity, force, null).ConfigureAwait(false);
                if (release is null || (!force && WasSkipped(activity, release.Tag)))
                {
                    return;
                }

                var path = await DownloadAsync(activity, release, null).ConfigureAwait(false);
                if (path is null)
                {
                    return;
                }

                activity.RunOnUiThread(() => ShowInstallDialog(activity, release, path));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AppLog.Write("update", "Автоматическая проверка обновления не удалась", exception);
            }
        });
    }

    /// <summary>Ручная проверка из «Общих» и «О программе»: проверяет, скачивает и предлагает установку.</summary>
    public static async Task CheckInteractiveAsync(Activity activity, Action<string> status)
    {
        try
        {
            var release = await CheckAsync(activity, force: true, status).ConfigureAwait(false);
            if (release is null)
            {
                return;
            }

            Report(activity, status, $"Доступна версия {release.Tag}. Скачиваю…");
            var path = await DownloadAsync(activity, release, status).ConfigureAwait(false);
            if (path is null)
            {
                return;
            }

            Report(activity, status, $"Версия {release.Tag} скачана.");
            activity.RunOnUiThread(() => ShowInstallDialog(activity, release, path));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Report(activity, status, "Ошибка проверки: " + exception.Message);
            AppLog.Write("update", "Ручная проверка обновления не удалась", exception);
        }
    }

    /// <summary>Фоновая проверка из обслуживания: без загрузки, только уведомление.</summary>
    public static async Task CheckInBackgroundAsync(Context context)
    {
        try
        {
            var preferences = Preferences(context);
            var last = ReadTime(preferences, KeyLastCheck);
            if (DateTime.UtcNow - last < AutoCheckInterval)
            {
                return;
            }

            WriteTime(preferences, KeyLastCheck, DateTime.UtcNow);
            var release = await AndroidUpdate.FindLatestAsync(CancellationToken.None).ConfigureAwait(false);
            if (release is null ||
                !AndroidUpdate.IsNewer(CurrentVersion(context), release.Tag) ||
                WasSkipped(context, release.Tag))
            {
                return;
            }

            UpdateNotifier.Available(context, release);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Write("update", "Фоновая проверка обновления не удалась", exception);
        }
    }

    private static async Task<AndroidUpdate.Release?> CheckAsync(
        Context context,
        bool force,
        Action<string>? status)
    {
        if (!await Gate.WaitAsync(0).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var preferences = Preferences(context);
            var now = DateTime.UtcNow;
            if (!force && now - ReadTime(preferences, KeyLastCheck) < AutoCheckInterval)
            {
                return null;
            }

            WriteTime(preferences, KeyLastCheck, now);
            Report(context, status, "Проверяю GitHub…");

            var release = await AndroidUpdate.FindLatestAsync(CancellationToken.None).ConfigureAwait(false);
            if (release is null)
            {
                Report(context, status,
                    "Не удалось проверить: нет сети или GitHub недоступен (попробуйте через узел).");
                return null;
            }

            var current = CurrentVersion(context);
            if (!AndroidUpdate.IsNewer(current, release.Tag))
            {
                Report(context, status, $"Установлена последняя версия ({current}).");
                return null;
            }

            Report(context, status, $"Доступна версия {release.Tag} (у вас {current}).");
            return release;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string?> DownloadAsync(
        Activity activity,
        AndroidUpdate.Release release,
        Action<string>? status)
    {
        var path = CachedPath(activity, release);

        // Уже скачанный APK переиспользуется: повторной загрузки 46 МБ нет.
        // Рядом лежит маркер «тег|размер», он создаётся только после проверки
        // SHA-256, поэтому повреждённый или чужой файл не будет установлен.
        if (IsCachedApk(release, path))
        {
            Report(activity, status, $"Версия {release.Tag} уже скачана.");
            UpdateNotifier.Done(activity, release, path);
            return path;
        }

        var progress = new Progress<double>(value =>
        {
            var percent = (int)Math.Round(Math.Clamp(value, 0, 1) * 100);
            UpdateNotifier.Progress(activity, release, percent);
            Report(activity, status, $"Скачиваю {release.Tag}: {percent}%");
        });

        try
        {
            UpdateNotifier.Progress(activity, release, 0);
            var downloaded = await AndroidUpdate.DownloadAsync(
                release, path, progress, CancellationToken.None).ConfigureAwait(false);
            WriteCacheMarker(release, downloaded);
            UpdateNotifier.Done(activity, release, downloaded);
            Report(activity, status, $"Версия {release.Tag} скачана.");
            return downloaded;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            UpdateNotifier.Cancel(activity);
            Report(activity, status, "Не удалось скачать обновление: " + exception.Message);
            AppLog.Write("update", "Загрузка APK не удалась", exception);
            return null;
        }
    }

    private static void ShowInstallDialog(Activity activity, AndroidUpdate.Release release, string path)
    {
        if (activity.IsFinishing || activity.IsDestroyed)
        {
            return;
        }

        var sizeMb = new FileInfo(path).Length / (1024d * 1024d);
        var dialog = new AlertDialog.Builder(activity)
            .SetTitle("Обновление VieNLink")
            .SetMessage(
                $"Скачана версия {release.Tag} ({sizeMb:0.0} МБ).\n\n" +
                "Установить поверх текущей? Настройки, источники и кэш узлов сохранятся.")
            .SetPositiveButton("Установить", (_, _) => TryInstall(activity, path))
            .SetNeutralButton("Открыть релиз", (_, _) => OpenUrl(activity, release.PageUrl))
            .SetNegativeButton("Позже", (_, _) => MarkSkipped(activity, release.Tag))
            .Create();
        dialog.Show();
    }

    private static void TryInstall(Activity activity, string path)
    {
        if (ApkInstaller.Install(activity, path))
        {
            Ui.Toast(activity, "Подтвердите установку в системном окне.");
        }
        else
        {
            Ui.Toast(activity, "Разрешите установку приложений из VieNLink и повторите.");
        }
    }

    private static string CachedPath(Context context, AndroidUpdate.Release release)
    {
        var directory = new Java.IO.File(context.CacheDir, "updates");
        directory.Mkdirs();
        return Path.Combine(directory.AbsolutePath, release.ApkName);
    }

    private static string MarkerPath(string apkPath) => apkPath + ".ok";

    private static bool IsCachedApk(AndroidUpdate.Release release, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var length = new FileInfo(path).Length;
            if (release.ApkSize > 0 && length != release.ApkSize)
            {
                return false;
            }

            var markerPath = MarkerPath(path);
            if (!File.Exists(markerPath))
            {
                return false;
            }

            // Маркер пишется после успешной SHA-256-проверки загрузки.
            return File.ReadAllText(markerPath).Trim() == $"{release.Tag}|{length}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteCacheMarker(AndroidUpdate.Release release, string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            File.WriteAllText(MarkerPath(path), $"{release.Tag}|{length}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("update", "Не удалось сохранить отметку скачанного APK", exception);
        }
    }

    public static string CurrentVersion(Context context)
    {
        try
        {
            return context.PackageManager?.GetPackageInfo(context.PackageName!, 0)?.VersionName
                   ?? AppInfo.Version;
        }
        catch (Exception)
        {
            return AppInfo.Version;
        }
    }

    private static ISharedPreferences Preferences(Context context) =>
        context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)
        ?? throw new InvalidOperationException("SharedPreferences недоступны.");

    private static DateTime ReadTime(ISharedPreferences preferences, string key)
    {
        var value = preferences.GetLong(key, 0);
        return value <= 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
    }

    private static void WriteTime(ISharedPreferences preferences, string key, DateTime value)
    {
        var editor = preferences.Edit();
        editor?.PutLong(key, new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeMilliseconds());
        editor?.Apply();
    }

    private static bool WasSkipped(Context context, string tag)
    {
        var preferences = Preferences(context);
        return preferences.GetString(KeySkippedTag, null) == tag &&
               DateTime.UtcNow - ReadTime(preferences, KeySkippedAt) < SkipInterval;
    }

    private static void MarkSkipped(Context context, string tag)
    {
        var editor = Preferences(context).Edit();
        editor?.PutString(KeySkippedTag, tag);
        editor?.PutLong(KeySkippedAt,
            new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds());
        editor?.Apply();
    }

    private static void Report(Context context, Action<string>? status, string text)
    {
        if (status is null)
        {
            return;
        }

        if (context is Activity activity)
        {
            activity.RunOnUiThread(() =>
            {
                if (!activity.IsFinishing && !activity.IsDestroyed)
                {
                    status(text);
                }
            });
        }
        else
        {
            status(text);
        }
    }

    private static void OpenUrl(Context context, string url)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url)!)
                .AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception exception)
        {
            Ui.Toast(context, "Не удалось открыть ссылку: " + exception.Message);
        }
    }
}
