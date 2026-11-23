using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace VieNLink;

/// <summary>
/// Геометрия окон: главное окно открывается в оптимальном размере, его можно
/// свернуть, развернуть и растянуть, а положение и положение плавающего виджета
/// запоминаются между запусками.
/// </summary>
public partial class MainWindow
{
    /// <summary>Фиксированный размер окна: подобран так, чтобы шапка и кнопки помещались.</summary>
    private const double PreferredWindowWidth = 980;

    private const double PreferredWindowHeight = 680;

    private const double MinimumWindowWidth = 953;
    private const double MinimumWindowHeight = 620;

    private readonly DispatcherTimer _placementSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    /// <summary>Подгоняет размер окна под экран и фиксирует его (только свернуть/восстановить).</summary>
    private void InitializeWindowGeometry()
    {
        var area = SystemParameters.WorkArea;
        var width = Math.Clamp(PreferredWindowWidth, MinimumWindowWidth, Math.Max(MinimumWindowWidth, area.Width - 40));
        var height = Math.Clamp(PreferredWindowHeight, MinimumWindowHeight, Math.Max(MinimumWindowHeight, area.Height - 60));

        // Размер в DIPs одинаков на любом масштабе экрана, поэтому окно не «прыгает» при смене DPI.
        // Обычный размер фиксирован: растянуть окно нельзя, но свернуть и развернуть можно.
        Width = width;
        Height = height;
        MinWidth = width;
        MaxWidth = double.PositiveInfinity;
        MinHeight = height;
        MaxHeight = double.PositiveInfinity;
        ResizeMode = ResizeMode.CanResize;
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(HookWindowMessages);
            }
        };

        RestoreWindowPosition();

        _placementSaveTimer.Tick += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SaveWindowPlacement();
        };

        LocationChanged += (_, _) => SchedulePlacementSave();
        StateChanged += (_, _) => SchedulePlacementSave();
    }

    private const int WmNcHitTest = 0x0084;
    private const int HtBorder = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    /// <summary>
    /// Убирает рамку изменения размера: у краёв окна возвращаем HTBORDER, поэтому
    /// растянуть его мышью нельзя. Кнопки «свернуть» и «развернуть» остаются рабочими —
    /// они не попадают в 6-пиксельную зону края.
    /// </summary>
    private IntPtr HookWindowMessages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmNcHitTest)
        {
            return IntPtr.Zero;
        }

        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        if (!GetWindowRect(hwnd, out var rect))
        {
            return IntPtr.Zero;
        }

        const int grip = 6;
        if (x < rect.Left + grip || x >= rect.Right - grip ||
            y < rect.Top + grip || y >= rect.Bottom - grip)
        {
            handled = true;
            return new IntPtr(HtBorder);
        }

        return IntPtr.Zero;
    }

    private void RestoreWindowPosition()
    {
        if (_settings.WindowLeft is not double savedLeft ||
            _settings.WindowTop is not double savedTop ||
            !double.IsFinite(savedLeft) ||
            !double.IsFinite(savedTop))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = ClampCoordinate(savedLeft, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenWidth, Width);
        Top = ClampCoordinate(savedTop, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenHeight, Height);
    }

    private static double ClampCoordinate(double coordinate, double screenStart, double screenSize, double windowSize)
    {
        var maximum = screenStart + Math.Max(0, screenSize - windowSize);
        return Math.Clamp(coordinate, screenStart, maximum);
    }

    private void SchedulePlacementSave()
    {
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    /// <summary>Пишет положение главного окна в settings.json; вызывается и при выходе.</summary>
    internal void SaveWindowPlacement()
    {
        var left = WindowState == WindowState.Normal ? Left : RestoreBounds.Left;
        var top = WindowState == WindowState.Normal ? Top : RestoreBounds.Top;
        if (double.IsFinite(left) && double.IsFinite(top))
        {
            _settings.WindowLeft = left;
            _settings.WindowTop = top;
            JsonFileStore.Write(AppPaths.SettingsPath, _settings);
        }
    }

    /// <summary>Виджет сохраняет своё место и состояние строки через главное окно.</summary>
    internal void SaveWidgetPlacement(double left, double top, bool collapsed)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top))
        {
            return;
        }

        _settings.WidgetLeft = left;
        _settings.WidgetTop = top;
        _settings.WidgetCollapsed = collapsed;
        JsonFileStore.Write(AppPaths.SettingsPath, _settings);
    }

    /// <summary>Положение виджета из настроек: null — виджет ещё не перемещали.</summary>
    internal (double? Left, double? Top, bool Collapsed) GetWidgetPlacement() =>
        (_settings.WidgetLeft, _settings.WidgetTop, _settings.WidgetCollapsed);

    /// <summary>Виджет поверх всех окон (по умолчанию включено).</summary>
    internal bool GetWidgetTopmost() => _settings.WidgetTopmost;

    /// <summary>
    /// Сохраняет режим «поверх всех окон» и синхронизирует галочку в «Общих»:
    /// переключать можно и из виджета, и из настроек.
    /// </summary>
    internal void SaveWidgetTopmost(bool topmost)
    {
        _settings.WidgetTopmost = topmost;
        JsonFileStore.Write(AppPaths.SettingsPath, _settings);
        if (WidgetTopmostCheck is not null && WidgetTopmostCheck.IsChecked != topmost)
        {
            _loadingSettings = true;
            try
            {
                WidgetTopmostCheck.IsChecked = topmost;
            }
            finally
            {
                _loadingSettings = false;
            }
        }

        if (_zapretWidget is not null)
        {
            _zapretWidget.Topmost = topmost;
            _zapretWidget.RefreshTopmostState();
        }
    }

    internal static double ClampToScreen(double coordinate, double screenStart, double screenSize, double windowSize) =>
        ClampCoordinate(coordinate, screenStart, screenSize, windowSize);
}
