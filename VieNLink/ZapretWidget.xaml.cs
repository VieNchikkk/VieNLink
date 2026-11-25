using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VieNLink;

/// <summary>
/// Плавающий виджет: три способа доступа к заблокированным сайтам — zapret,
/// свой обход DPI и видео YouTube через сервер. Активная кнопка подсвечивается,
/// ниже мелко написано, какой режим работает сейчас и по какой стратегии.
/// В свёрнутом виде остаются точка состояния (зелёная — включено, красная — нет)
/// и название работающего режима.
/// </summary>
public partial class ZapretWidget : Window
{
    private readonly MainWindow _main;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _placementSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _collapsed;

    public ZapretWidget(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        var placement = _main.GetWidgetPlacement();
        _collapsed = placement.Collapsed;
        RefreshTopmostState();
        _main.ZapretStateChanged += OnStateChanged;
        _timer.Tick += (_, _) => Refresh();
        _placementSaveTimer.Tick += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SavePlacement();
        };
        LocationChanged += (_, _) =>
        {
            _placementSaveTimer.Stop();
            _placementSaveTimer.Start();
        };
        Closed += OnClosed;
        Loaded += OnLoaded;
        ApplyCollapsed();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyCollapsed();
        UpdateLayout();

        // Виджет открывается там, где его оставили. Первый запуск — правый нижний угол.
        var (savedLeft, savedTop, _) = _main.GetWidgetPlacement();
        if (savedLeft is double left && savedTop is double top &&
            double.IsFinite(left) && double.IsFinite(top))
        {
            Left = MainWindow.ClampToScreen(left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenWidth, ActualWidth);
            Top = MainWindow.ClampToScreen(top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenHeight, ActualHeight);
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        }

        // Состояние меняют разные подсистемы (сервер, свой обход, видео),
        // поэтому кроме события обновляемся и по таймеру.
        _timer.Start();
        Refresh();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _placementSaveTimer.Stop();
        SavePlacement();
        _main.ZapretStateChanged -= OnStateChanged;
    }

    private void SavePlacement()
    {
        if (IsLoaded && double.IsFinite(Left) && double.IsFinite(Top))
        {
            _main.SaveWidgetPlacement(Left, Top, _collapsed);
        }
    }

    private void OnStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(Refresh);
            return;
        }

        Refresh();
    }

    private void ApplyCollapsed()
    {
        WidgetBody.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        WidgetCollapseButton.Content = _collapsed ? "▸" : "▾";
        WidgetTitle.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        WidgetMiniInfo.Visibility = _collapsed ? Visibility.Visible : Visibility.Collapsed;
        Width = _collapsed ? 162 : 212;
        Refresh();
    }

    private void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        ApplyCollapsed();
        UpdateLayout();
        if (IsLoaded)
        {
            Left = MainWindow.ClampToScreen(Left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenWidth, ActualWidth);
            Top = MainWindow.ClampToScreen(Top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenHeight, ActualHeight);
        }

        _main.SaveWidgetPlacement(Left, Top, _collapsed);
    }

    private void Refresh()
    {
        var busy = _main.WidgetBusy;
        var mode = _main.ActiveModeName;
        var active = mode.Length > 0;

        WidgetDot.Fill = (Brush)FindResource(active ? "PositiveTextBrush" : "DangerBrush");
        WidgetTitle.Text = "VieNLink";
        WidgetMiniInfo.Text = _main.WidgetMiniInfoText;

        WidgetYoutubeCount.Text = _main.YoutubeNodeCount.ToString();
        WidgetDiscordCount.Text = _main.DiscordNodeCount.ToString();
        WidgetUniversalCount.Text = _main.UniversalNodeCount.ToString();
        WidgetTelegramCount.Text = _main.TelegramNodeCount.ToString();

        ApplyButtonState(WidgetYoutubeButton, _main.IsServiceConnected("youtube"), busy);
        ApplyButtonState(WidgetDiscordButton, _main.IsServiceConnected("discord"), busy);
        ApplyButtonState(WidgetUniversalButton, _main.IsServiceConnected("universal"), busy);
        ApplyButtonState(WidgetTelegramButton, _main.IsServiceConnected("telegram"), busy);
        ApplyButtonState(WidgetZapretButton, _main.ZapretRunning, busy);
        ApplyButtonState(WidgetDesyncButton, _main.DesyncRunning, busy);

        var detail = _main.ActiveModeDetail;
        WidgetStatus.Text = active
            ? "Работает: " + mode + (detail.Length == 0 ? string.Empty : " · " + detail)
            : busy
                ? "Идёт проверка или подбор стратегии…"
                : "Ничего не включено — выберите способ";
    }

    /// <summary>Работающий режим выделяется акцентом, остальные остаются обычными.</summary>
    private static void ApplyButtonState(Button button, bool active, bool busy)
    {
        if (active)
        {
            button.SetResourceReference(BackgroundProperty, "AccentBrush");
            button.SetResourceReference(ForegroundProperty, "AccentContrastBrush");
            button.SetResourceReference(BorderBrushProperty, "AccentBrush");
            button.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            button.ClearValue(BackgroundProperty);
            button.ClearValue(ForegroundProperty);
            button.ClearValue(BorderBrushProperty);
            button.ClearValue(FontWeightProperty);
        }

        button.IsEnabled = !busy;
    }

    private async void OnZapretClick(object sender, RoutedEventArgs e) =>
        await RunAsync(_main.WidgetToggleZapretAsync);

    private async void OnDesyncClick(object sender, RoutedEventArgs e) =>
        await RunAsync(_main.WidgetToggleDesyncAsync);

    private async void OnYoutubeClick(object sender, RoutedEventArgs e) =>
        await RunAsync(() => _main.WidgetConnectServiceAsync("youtube"));

    private async void OnDiscordClick(object sender, RoutedEventArgs e) =>
        await RunAsync(() => _main.WidgetConnectServiceAsync("discord"));

    private async void OnUniversalClick(object sender, RoutedEventArgs e) =>
        await RunAsync(() => _main.WidgetConnectServiceAsync("universal"));

    private async void OnTelegramClick(object sender, RoutedEventArgs e) =>
        await RunAsync(() => _main.WidgetConnectServiceAsync("telegram"));

    private async Task RunAsync(Func<Task> action)
    {
        WidgetZapretButton.IsEnabled = false;
        WidgetDesyncButton.IsEnabled = false;
        WidgetYoutubeButton.IsEnabled = false;
        WidgetDiscordButton.IsEnabled = false;
        WidgetUniversalButton.IsEnabled = false;
        WidgetTelegramButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // Отмена — штатный сценарий, сообщать нечего.
        }
        catch (Exception exception)
        {
            AppLog.Write("widget", "Ошибка действия из виджета", exception);
            MessageBox.Show(
                this,
                "Не удалось выполнить действие:\n" + exception.Message,
                "VieNLink",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            Refresh();
        }
    }

    /// <summary>
    /// «Поверх всех окон»: по умолчанию включено, чтобы виджет не прятался за игрой
    /// или браузером. Отключается закрепом в шапке виджета или галочкой в «Общих».
    /// </summary>
    public void RefreshTopmostState()
    {
        var topmost = _main.GetWidgetTopmost();
        Topmost = topmost;
        if (WidgetPinButton is not null)
        {
            WidgetPinButton.Content = topmost ? "📌" : "📍";
            WidgetPinButton.Opacity = topmost ? 1 : 0.45;
            WidgetPinButton.ToolTip = topmost
                ? "Виджет поверх всех окон: включено. Нажмите, чтобы отключить"
                : "Виджет поверх всех окон: выключено. Нажмите, чтобы включить";
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e) =>
        _main.SaveWidgetTopmost(!_main.GetWidgetTopmost());

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
