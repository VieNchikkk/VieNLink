using System.Windows;
using System.Windows.Controls;

namespace VieNLink;

/// <summary>Настройки браузера: запуск, вид, поведение, приватность, дополнительные параметры.</summary>
public sealed partial class BrowserSettingsWindow : Window
{
    private static readonly double[] ZoomPresets = { 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 2.0 };

    private readonly List<RadioButton> _zoomButtons = new();

    internal BrowserSettingsWindow(BrowserSettings settings)
    {
        InitializeComponent();
        HomePage = settings.HomePage;
        RestoreTabs = settings.RestoreTabs;
        ClearDataOnStart = settings.ClearDataOnStart;
        Zoom = settings.Zoom;
        ShowBookmarksBar = settings.ShowBookmarksBar;
        ShowFavicons = settings.ShowFavicons;
        ShowLinkStatusBar = settings.ShowLinkStatusBar;
        CloseWithLastTab = settings.CloseWithLastTab;
        SaveHistory = settings.SaveHistory;
        BlockSensitivePermissions = settings.BlockSensitivePermissions;
        RequireVpn = settings.RequireVpn;
        AutoConnectOnFailure = settings.AutoConnectOnFailure;
        EnableAutofill = settings.EnableAutofill;
        EnableJavaScript = settings.EnableJavaScript;
        EnableDevTools = settings.EnableDevTools;

        foreach (var zoom in ZoomPresets)
        {
            var button = new RadioButton
            {
                Content = $"{Math.Round(zoom * 100)}%",
                GroupName = "zoom",
                Style = (Style)FindResource("AppRadioButton"),
                Margin = new Thickness(0, 0, 14, 6),
                Tag = zoom,
                IsChecked = Math.Abs(settings.Zoom - zoom) < 0.001,
            };
            _zoomButtons.Add(button);
            ZoomPanel.Children.Add(button);
        }

        if (_zoomButtons.All(button => button.IsChecked != true))
        {
            _zoomButtons.First(button => Math.Abs((double)button.Tag - 1.0) < 0.001).IsChecked = true;
        }

        HomeBox.Text = settings.HomePage;
        RestoreCheck.IsChecked = settings.RestoreTabs;
        ClearDataCheck.IsChecked = settings.ClearDataOnStart;
        BookmarksBarCheck.IsChecked = settings.ShowBookmarksBar;
        FaviconsCheck.IsChecked = settings.ShowFavicons;
        LinkStatusCheck.IsChecked = settings.ShowLinkStatusBar;
        CloseLastTabCheck.IsChecked = settings.CloseWithLastTab;
        HistoryCheck.IsChecked = settings.SaveHistory;
        BlockPermissionsCheck.IsChecked = settings.BlockSensitivePermissions;
        VpnOnlySettingsCheck.IsChecked = settings.RequireVpn;
        AutoConnectCheck.IsChecked = settings.AutoConnectOnFailure;
        AutofillCheck.IsChecked = settings.EnableAutofill;
        JavaScriptCheck.IsChecked = settings.EnableJavaScript;
        DevToolsCheck.IsChecked = settings.EnableDevTools;
    }

    public string HomePage { get; private set; }

    public bool RestoreTabs { get; private set; }

    public bool ClearDataOnStart { get; private set; }

    public double Zoom { get; private set; }

    public bool ShowBookmarksBar { get; private set; }

    public bool ShowFavicons { get; private set; }

    public bool ShowLinkStatusBar { get; private set; }

    public bool CloseWithLastTab { get; private set; }

    public bool SaveHistory { get; private set; }

    public bool BlockSensitivePermissions { get; private set; }

    public bool RequireVpn { get; private set; }

    public bool AutoConnectOnFailure { get; private set; }

    public bool EnableAutofill { get; private set; }

    public bool EnableJavaScript { get; private set; }

    public bool EnableDevTools { get; private set; }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // Пустое поле — стартовая страница VieNLink (поиск Google и закладки).
        var home = HomeBox.Text.Trim();
        if (home.Length > 0 &&
            (!Uri.TryCreate(home, UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            MessageBox.Show(this, "Домашняя страница должна быть адресом вида https://example.com/ " +
                                  "или пустой — тогда открывается стартовая страница VieNLink.",
                "Настройки браузера", MessageBoxButton.OK, MessageBoxImage.Warning);
            HomeBox.Focus();
            return;
        }

        Zoom = _zoomButtons.FirstOrDefault(button => button.IsChecked == true) is { Tag: double value }
            ? value
            : 1.0;
        HomePage = home;
        RestoreTabs = RestoreCheck.IsChecked == true;
        ClearDataOnStart = ClearDataCheck.IsChecked == true;
        ShowBookmarksBar = BookmarksBarCheck.IsChecked == true;
        ShowFavicons = FaviconsCheck.IsChecked == true;
        ShowLinkStatusBar = LinkStatusCheck.IsChecked == true;
        CloseWithLastTab = CloseLastTabCheck.IsChecked == true;
        SaveHistory = HistoryCheck.IsChecked == true;
        BlockSensitivePermissions = BlockPermissionsCheck.IsChecked == true;
        RequireVpn = VpnOnlySettingsCheck.IsChecked == true;
        AutoConnectOnFailure = AutoConnectCheck.IsChecked == true;
        EnableAutofill = AutofillCheck.IsChecked == true;
        EnableJavaScript = JavaScriptCheck.IsChecked == true;
        EnableDevTools = DevToolsCheck.IsChecked == true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
