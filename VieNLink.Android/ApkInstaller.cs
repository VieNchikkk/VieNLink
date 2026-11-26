using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Установка скачанного APK через PackageInstaller: без FileProvider и без
/// доступа к общей памяти. Android показывает системное подтверждение установки,
/// поэтому обновление не может быть полностью «молчаливым».
/// </summary>
internal static class ApkInstaller
{
    public const string ActionInstallResult = "com.vienlink.app.INSTALL_RESULT";
    public const string ActionInstallNow = "com.vienlink.app.INSTALL_NOW";
    public const string ExtraApkPath = "apk-path";

    public static bool Install(Context context, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                AppLog.Write("update", "APK для установки не найден: " + path);
                return false;
            }

            var packageManager = context.PackageManager;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O &&
                packageManager?.CanRequestPackageInstalls() == false)
            {
                OpenUnknownSourcesSettings(context);
                return false;
            }

            // Системный установщик по content://: PackageInstaller на части
            // прошивок (ColorOS) не показывает окно подтверждения для сессии,
            // созданной приложением, а ACTION_VIEW работает предсказуемо.
            var name = Android.Net.Uri.Encode(Path.GetFileName(path));
            var uri = Android.Net.Uri.Parse($"content://{UpdateFileProvider.Authority}/{name}");
            if (uri is not null)
            {
                try
                {
                    var view = new Intent(Intent.ActionView)
                        .SetDataAndType(uri, "application/vnd.android.package-archive")
                        .AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);

                    // На телефонах с двумя установщиками (Google и магазин OEM)
                    // без явного пакета открывается выбор «Открыть с помощью».
                    var installerPackage = FindSystemInstaller(packageManager, view);
                    if (!string.IsNullOrWhiteSpace(installerPackage))
                    {
                        view.SetPackage(installerPackage);
                    }

                    context.StartActivity(view);
                    DeviceProfile.LogRotationState("перед установщиком APK");
                    AppLog.Write("update", "Открыт системный установщик APK: " + Path.GetFileName(path));
                    return true;
                }
                catch (ActivityNotFoundException exception)
                {
                    AppLog.Write("update", "Системный установщик не найден, пробую PackageInstaller", exception);
                }
            }

            var installer = packageManager?.PackageInstaller;
            if (installer is null)
            {
                AppLog.Write("update", "PackageInstaller недоступен на устройстве");
                return false;
            }

            var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
            parameters.SetAppPackageName(context.PackageName);
            var sessionId = installer.CreateSession(parameters);

            using (var session = installer.OpenSession(sessionId))
            {
                if (session is null)
                {
                    AppLog.Write("update", "Не удалось открыть сессию установки APK");
                    return false;
                }

                using (var input = File.OpenRead(path))
                using (var output = session.OpenWrite("base.apk", 0, input.Length))
                {
                    input.CopyTo(output);
                    session.Fsync(output);
                    output.Close();
                }

                var result = new Intent(context, typeof(UpdateInstallReceiver))
                    .SetAction(ActionInstallResult);
                var pending = PendingIntent.GetBroadcast(
                    context, 7102, result,
                    PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
                if (pending is null)
                {
                    AppLog.Write("update", "Не удалось создать PendingIntent установки");
                    return false;
                }

                session.Commit(pending.IntentSender);
            }

            AppLog.Write("update", "Сессия установки APK открыта: " + Path.GetFileName(path));
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Write("update", "Не удалось запустить установку APK", exception);
            return false;
        }
    }

    private static string? FindSystemInstaller(PackageManager? packageManager, Intent intent)
    {
        if (packageManager is null)
        {
            return null;
        }

        try
        {
            var activities = packageManager.QueryIntentActivities(intent, 0);
            foreach (var activity in activities)
            {
                var package = activity.ActivityInfo?.PackageName;
                if (package is "com.google.android.packageinstaller" or "com.android.packageinstaller")
                {
                    return package;
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("update", "Не удалось выбрать системный установщик", exception);
        }

        return null;
    }

    private static void OpenUnknownSourcesSettings(Context context)
    {
        try
        {
            var intent = new Intent(
                Settings.ActionManageUnknownAppSources,
                Android.Net.Uri.Parse("package:" + context.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
            Ui.Toast(context, "Разрешите установку приложений из VieNLink и нажмите «Проверить обновление» снова.");
        }
        catch (Exception exception)
        {
            AppLog.Write("update", "Не удалось открыть настройки неизвестных источников", exception);
            try
            {
                var fallback = new Intent(Settings.ActionManageApplicationsSettings)
                    .AddFlags(ActivityFlags.NewTask);
                context.StartActivity(fallback);
            }
            catch (Exception)
            {
                Ui.Toast(context, "Разрешите установку из VieNLink в настройках Android.");
            }
        }
    }
}

/// <summary>Результат установки APK от PackageInstaller.</summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
[IntentFilter(new[] { ApkInstaller.ActionInstallResult, ApkInstaller.ActionInstallNow })]
public sealed class UpdateInstallReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null)
        {
            return;
        }

        // Нажатие на уведомление «Обновление скачано».
        if (intent.Action == ApkInstaller.ActionInstallNow)
        {
            var apkPath = intent.GetStringExtra(ApkInstaller.ExtraApkPath);
            UpdateNotifier.Cancel(context);
            if (!string.IsNullOrWhiteSpace(apkPath) && ApkInstaller.Install(context, apkPath))
            {
                Ui.Toast(context, "Подтвердите установку в системном окне.");
            }

            return;
        }

        var status = intent.GetIntExtra(
            PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
        if (status == (int)PackageInstallStatus.Success)
        {
            AppLog.Write("update", "Обновление установлено");
            // Фиксируем состояние системного автоповорота сразу после установки:
            // если после обновления он «включается», в журнале будет видно, на каком
            // шаге это произошло (до установщика, после установки или при старте).
            DeviceProfile.LogRotationState("после установки обновления");
            Ui.Toast(context, "VieNLink обновлён.");
            return;
        }

        if (status == (int)PackageInstallStatus.PendingUserAction)
        {
            return;
        }

        var message = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage);
        AppLog.Write("update", $"Установка APK не удалась ({status}): {message}");
        Ui.Toast(context, "Не удалось установить обновление: " + (message ?? "код " + status));
    }
}
