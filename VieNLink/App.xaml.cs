using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace VieNLink;

public partial class App : Application
{
    private Mutex? _mutex;

    /// <summary>Общие настройки: читаются один раз при старте и сохраняются из окна.</summary>
    internal static AppSettings Settings { get; set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Повышение прав запускает новый экземпляр до выхода старого:
        // даём старому до 5 секунд освободить мьютекс, иначе пользователь увидит
        // ложное «VieNLink уже запущен» сразу после UAC.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            _mutex = new Mutex(true, @"Local\VieNLink.SingleInstance", out var createdNew);
            if (createdNew)
            {
                break;
            }

            _mutex.Dispose();
            _mutex = null;
            if (DateTime.UtcNow >= deadline)
            {
                MessageBox.Show("VieNLink уже запущен.", "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            Thread.Sleep(150);
        }

        AppLog.Initialize();
        AppPaths.EnsureDirectories();

        // Если прошлый запуск упал с включённым прокси, возвращаем настройки Windows.
        try
        {
            if (SystemProxy.RecoverIfNeeded())
            {
                AppLog.Write("app", "Прокси восстановлен после аварийного завершения");
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("app", "Не удалось восстановить системный прокси", exception);
        }

        Settings = JsonFileStore.Read<AppSettings>(AppPaths.SettingsPath) ?? new AppSettings();
        if (SettingsUpgrade.Apply(Settings))
        {
            JsonFileStore.Write(AppPaths.SettingsPath, Settings);
        }
        ApplyTheme(Settings.Theme);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Write("app", "Необработанная ошибка", args.ExceptionObject as Exception);

        AppLog.Write("app", "Запуск " + AppInfo.DisplayName);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Write("app", "Выход");
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Подмена словаря темы: стили остаются на месте.</summary>
    public static void ApplyTheme(string? theme)
    {
        var isLight = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        var source = new Uri(isLight ? "Themes/LightTheme.xaml" : "Themes/DarkTheme.xaml", UriKind.Relative);
        var dictionaries = Current.Resources.MergedDictionaries;
        for (var index = 0; index < dictionaries.Count; index++)
        {
            var original = dictionaries[index].Source?.OriginalString ?? string.Empty;
            if (original.Contains("Themes/DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                original.Contains("Themes/LightTheme.xaml", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries[index] = new ResourceDictionary { Source = source };
                return;
            }
        }

        dictionaries.Add(new ResourceDictionary { Source = source });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("app", "Ошибка в UI-потоке", e.Exception);
        MessageBox.Show(
            "Произошла ошибка:\n" + e.Exception.Message + "\n\nПодробности в журнале: " + Path.Combine(AppPaths.LogsDirectory),
            "VieNLink",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
