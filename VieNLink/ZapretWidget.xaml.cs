using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VieNLink;

/// <summary>
/// Плавающий виджет: три способа доступа к заблокированным сайтам — zapret,
/// свой обход DPI и видео YouTube через узел. Активная кнопка подсвечивается,
/// ниже мелко написано, какой режим работает сейчас и по какой стратегии.
/// В свёрнутом виде остаются точка состояния (зелёная — включено, красная — нет)
/// и название работающего режима.
/// </summary>
public partial class ZapretWidget : Window
{
    private readonly MainWindow _main;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _collapsed = _lastCollapsed;
    private static bool _lastCollapsed;

    public ZapretWidget(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _main.ZapretStateChanged += OnStateChanged;
        _timer.Tick += (_, _) => Refresh();
        Closed += OnClosed;
        Loaded += OnLoaded;
        ApplyCollapsed();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyCollapsed();
        UpdateLayout();

        // Первый показ — в правом нижнем углу рабочей области.
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;

        // Состояние меняют разные подсистемы (узел, свой обход, видео),
        // поэтому кроме события обновляемся и по таймеру.
        _timer.Start();
        Refresh();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _main.ZapretStateChanged -= OnStateChanged;
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
        Width = _collapsed ? 168 : 212;
        Refresh();
    }

    private void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        _lastCollapsed = _collapsed;
        ApplyCollapsed();
    }

    private void Refresh()
    {
        var busy = _main.WidgetBusy;
        var mode = _main.ActiveModeName;
        var active = mode.Length > 0;

        WidgetDot.Fill = (Brush)FindResource(active ? "PositiveTextBrush" : "DangerBrush");
        WidgetTitle.Text = _collapsed ? (active ? mode : "Выключено") : "Обход DPI";

        ApplyButtonState(WidgetZapretButton, _main.ZapretRunning, busy);
        ApplyButtonState(WidgetDesyncButton, _main.DesyncRunning, busy);
        ApplyButtonState(WidgetVideoButton, _main.VideoModeActive, busy);

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

    private async void OnVideoClick(object sender, RoutedEventArgs e) =>
        await RunAsync(_main.WidgetToggleVideoAsync);

    private async Task RunAsync(Func<Task> action)
    {
        WidgetZapretButton.IsEnabled = false;
        WidgetDesyncButton.IsEnabled = false;
        WidgetVideoButton.IsEnabled = false;
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

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
