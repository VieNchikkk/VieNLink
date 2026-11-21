using System.Windows;
using System.Windows.Threading;

namespace VieNLink;

/// <summary>
/// Геометрия окон: главное окно открывается в оптимальном размере, его можно
/// свернуть, развернуть и растянуть, а положение и положение плавающего виджета
/// запоминаются между запусками.
/// </summary>
public partial class MainWindow
{
    /// <summary>Оптимальная ширина окна: помещается на 1080p и не распахивается на весь экран.</summary>
    private const double PreferredWindowWidth = 1240;

    /// <summary>Оптимальная высота окна: вкладки и таблицы видны без прокрутки на 1080p.</summary>
    private const double PreferredWindowHeight = 820;

    private const double MinimumWindowWidth = 820;
    private const double MinimumWindowHeight = 620;

    private readonly DispatcherTimer _placementSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    /// <summary>Подгоняет размер окна под экран, затем фиксирует его.</summary>
    private void InitializeWindowGeometry()
    {
        var area = SystemParameters.WorkArea;
        var width = Math.Clamp(PreferredWindowWidth, MinimumWindowWidth, Math.Max(MinimumWindowWidth, area.Width - 40));
        var height = Math.Clamp(PreferredWindowHeight, MinimumWindowHeight, Math.Max(MinimumWindowHeight, area.Height - 40));

        // Размер в DIPs одинаков на любом масштабе экрана, поэтому окно не «прыгает» при смене DPI.
        Width = width;
        Height = height;
        // Стартовый размер — рекомендуемый, но пользователь может свернуть,
        // развернуть на весь экран и изменить размер окна.
        MinWidth = MinimumWindowWidth;
        MinHeight = MinimumWindowHeight;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        ResizeMode = ResizeMode.CanResize;

        RestoreWindowPosition();

        _placementSaveTimer.Tick += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SaveWindowPlacement();
        };

        LocationChanged += (_, _) => SchedulePlacementSave();
        StateChanged += (_, _) => SchedulePlacementSave();
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
        if (double.IsFinite(Left) && double.IsFinite(Top))
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
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

    internal static double ClampToScreen(double coordinate, double screenStart, double screenSize, double windowSize) =>
        ClampCoordinate(coordinate, screenStart, screenSize, windowSize);
}
