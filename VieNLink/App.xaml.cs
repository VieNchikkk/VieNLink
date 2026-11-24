using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace VieNLink;

public partial class App : Application
{
    private const string ShowWindowEventName = @"Local\VieNLink.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showWindowEvent;
    private CancellationTokenSource? _showWindowCts;

    /// <summary>Общие настройки: читаются один раз при старте и сохраняются из окна.</summary>
    internal static AppSettings Settings { get; set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Повышение прав запускает новый экземпляр до выхода старого:
        // даём старому до 5 секунд освободить мьютекс, иначе пользователь увидит
        // ложное «VieNLink уже запущен» сразу после UAC.
        // Обычный повторный запуск (ярлык, закреплённая кнопка на панели задач)
        // не должен показывать «уже запущен»: будим окно первого экземпляра.
        // Аргументы повышения прав — отдельный случай: старый экземпляр сейчас
        // завершится, и новый должен продолжить работу, а не выйти.
        if (!IsElevationRestart() && NotifyExistingInstance())
        {
            Shutdown();
            return;
        }

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

        StartShowWindowListener();

        AppLog.Initialize();
        AppPaths.EnsureDirectories();

        // До создания окон: закреплённый ярлык и ярлык с рабочего стола должны
        // открывать одну и ту же кнопку на панели задач.
        TaskbarIdentity.Apply();

        // Ядро не знает про платформу: подставляем DPAPI до первого чтения настроек и источников.
        SecretProtector.Current = new DpapiSecretProtector();

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

        // Локальный канал для отдельного приложения «VieNLink Браузер»: состояние и команды VPN.
        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is MainWindow main)
            {
                VpnPipeServer.Start(main);
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Write("app", "Выход");
        _showWindowCts?.Cancel();
        _showWindowEvent?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Показывает и активирует главное окно по сигналу второго экземпляра.</summary>
    private void StartShowWindowListener()
    {
        try
        {
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            _showWindowCts = new CancellationTokenSource();
            var token = _showWindowCts.Token;
            _ = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    if (!_showWindowEvent.WaitOne(500))
                    {
                        continue;
                    }

                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (MainWindow is MainWindow window)
                            {
                                window.ShowFromTray();
                            }
                        });
                    }
                    catch (Exception exception)
                    {
                        AppLog.Write("app", "Не удалось показать окно по запросу", exception);
                    }
                }
            });
        }
        catch (Exception exception)
        {
            AppLog.Write("app", "Не удалось запустить обработчик повторного запуска", exception);
        }
    }

    private static bool NotifyExistingInstance()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var existing))
                {
                    using (existing)
                    {
                        existing.Set();
                    }

                    return true;
                }
            }
            catch (Exception)
            {
                // Первый экземпляр ещё создаёт событие — подождём немного.
            }

            Thread.Sleep(100);
        }

        return false;
    }

    /// <summary>Запуск с аргументом повышения прав: старый экземпляр завершается сам.</summary>
    private static bool IsElevationRestart()
    {
        var arguments = Environment.GetCommandLineArgs();
        return arguments.Any(argument => argument.Equals("--tunnel", StringComparison.OrdinalIgnoreCase) ||
                                         argument.Equals("--tun-bypass", StringComparison.OrdinalIgnoreCase) ||
                                         argument.Equals("--tun-bypass-tune", StringComparison.OrdinalIgnoreCase) ||
                                         argument.Equals("--store-loopback", StringComparison.OrdinalIgnoreCase) ||
                                         argument.Equals("--desync", StringComparison.OrdinalIgnoreCase) ||
                                         argument.Equals("--zapret", StringComparison.OrdinalIgnoreCase));
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
