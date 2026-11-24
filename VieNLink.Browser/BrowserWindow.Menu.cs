using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VieNLink;

/// <summary>
/// Меню браузера в духе Яндекс Браузера: тёмная панель с иконками и «Закрыть браузер».
/// </summary>
public partial class BrowserWindow
{
    private const string IconHistory =
        "M13,3A9,9 0 0,0 4,12H1L4.89,15.89L4.96,16.03L9,12H6A7,7 0 0,1 13,5A7,7 0 0,1 20,12A7,7 0 0,1 13,19C11.07,19 9.32,18.21 8.06,16.94L6.64,18.36C8.27,19.99 10.51,21 13,21A9,9 0 0,0 22,12A9,9 0 0,0 13,3M12,8V13L16.25,15.15L17,13.92L13.5,12.25V8H12Z";

    private const string IconBookmarks =
        "M17,3H7A2,2 0 0,0 5,5V21L12,18L19,21V5C19,3.89 18.1,3 17,3Z";

    private const string IconDownloads =
        "M5,20H19V18H5M19,9H15V3H9V9H5L12,16L19,9Z";

    private const string IconPlus =
        "M19,13H13V19H11V13H5V11H11V5H13V11H19V13Z";

    private const string IconSettings =
        "M12,15.5A3.5,3.5 0 0,1 8.5,12A3.5,3.5 0 0,1 12,8.5A3.5,3.5 0 0,1 15.5,12A3.5,3.5 0 0,1 12,15.5M19.43,12.97C19.47,12.65 19.5,12.33 19.5,12C19.5,11.67 19.47,11.34 19.43,11L21.54,9.37C21.73,9.22 21.78,8.95 21.66,8.73L19.66,5.27C19.54,5.05 19.27,4.96 19.05,5.05L16.56,6.05C16.04,5.66 15.5,5.32 14.87,5.07L14.5,2.42C14.46,2.18 14.25,2 14,2H10C9.75,2 9.54,2.18 9.5,2.42L9.13,5.07C8.5,5.32 7.96,5.66 7.44,6.05L4.95,5.05C4.73,4.96 4.46,5.05 4.34,5.27L2.34,8.73C2.21,8.95 2.27,9.22 2.46,9.37L4.57,11C4.53,11.34 4.5,11.67 4.5,12C4.5,12.33 4.53,12.65 4.57,12.97L2.46,14.63C2.27,14.78 2.21,15.05 2.34,15.27L4.34,18.73C4.46,18.95 4.73,19.03 4.95,18.95L7.44,17.94C7.96,18.34 8.5,18.68 9.13,18.93L9.5,21.58C9.54,21.82 9.75,22 10,22H14C14.25,22 14.46,21.82 14.5,21.58L14.87,18.93C15.5,18.67 16.04,18.34 16.56,17.94L19.05,18.95C19.27,19.03 19.54,18.95 19.66,18.73L21.66,15.27C21.78,15.05 21.73,14.78 21.54,14.63L19.43,12.97Z";

    private const string IconMore =
        "M6,10A2,2 0 0,0 4,12A2,2 0 0,0 6,14A2,2 0 0,0 8,12A2,2 0 0,0 6,10M12,10A2,2 0 0,0 10,12A2,2 0 0,0 12,14A2,2 0 0,0 14,12A2,2 0 0,0 12,10M18,10A2,2 0 0,0 16,12A2,2 0 0,0 18,14A2,2 0 0,0 20,12A2,2 0 0,0 18,10Z";

    private const string IconPower =
        "M16.56,5.44L15.11,6.89C16.84,7.94 18,9.83 18,12A6,6 0 0,1 12,18A6,6 0 0,1 6,12C6,9.83 7.16,7.94 8.88,6.88L7.44,5.44C5.36,6.88 4,9.28 4,12A8,8 0 0,0 12,20A8,8 0 0,0 20,12C20,9.28 18.64,6.88 16.56,5.44M13,3H11V13H13V3Z";

    private void ToggleBrowserMenu()
    {
        if (BrowserMenuPopup.IsOpen)
        {
            BrowserMenuPopup.IsOpen = false;
            return;
        }

        BuildBrowserMenu();

        // Меню выравниваем по правому краю кнопки ☰, чтобы оно не выходило за окно.
        BrowserMenuPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
        BrowserMenuPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) => new[]
        {
            new System.Windows.Controls.Primitives.CustomPopupPlacement(
                new Point(targetSize.Width - popupSize.Width, targetSize.Height + 6),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
        };
        BrowserMenuPopup.IsOpen = true;
    }

    private void BuildBrowserMenu()
    {
        BrowserMenuPanel.Children.Clear();
        AddMenuRow(IconPlus, "Новая вкладка", () => _ = CreateTabAsync(_settings.HomeUrl), "Ctrl+T");
        AddMenuSeparator();
        AddMenuRow(IconHistory, "История", OpenHistoryPage, "Ctrl+H");
        AddMenuRow(IconBookmarks, "Закладки", null, null, ShowBookmarksInMenu);
        AddMenuRow(IconDownloads, "Загрузки", OpenDownloads, "Ctrl+J");
        AddMenuSeparator();
        AddMenuRow(IconMore, "Дополнительно", null, null, ShowMoreInMenu);
        AddMenuRow(IconSettings, "Настройки", OpenSettingsPage);
        AddMenuSeparator();
        AddMenuRow(IconPower, "Закрыть браузер", Close);
    }

    private void ShowBookmarksInMenu()
    {
        AddMenuSeparator();
        if (_settings.Bookmarks.Count == 0)
        {
            AddMenuNote("Пока нет закладок — нажмите ☆ на панели.");
            return;
        }

        foreach (var bookmark in _settings.Bookmarks.Take(8))
        {
            var url = bookmark.Url;
            AddMenuRow(null, bookmark.Title, () => Navigate(url));
        }
    }

    private void ShowMoreInMenu()
    {
        AddMenuSeparator();
        AddMenuRow(null, "Увеличить масштаб", () => SetZoom(_zoom + 0.1), "Ctrl++");
        AddMenuRow(null, "Уменьшить масштаб", () => SetZoom(_zoom - 0.1), "Ctrl+-");
        AddMenuRow(null, "Обычный масштаб", () => SetZoom(1.0), "Ctrl+0");
        AddMenuRow(null, "Очистить кэш и cookie…", () => OnClearDataClick(this, new RoutedEventArgs()));
        AddMenuRow(null, "О браузере", () => OnAboutClick(this, new RoutedEventArgs()));
    }

    private void AddMenuRow(string? icon, string title, Action? action, string? gesture = null,
        Action? expand = null)
    {
        var row = new Button { Style = (Style)FindResource("MenuRowButton") };
        var panel = new DockPanel();

        if (gesture is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = gesture,
                FontSize = 11.5,
                Margin = new Thickness(12, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("ChromeMutedBrush"),
            });
            DockPanel.SetDock(panel.Children[^1], Dock.Right);
        }

        if (expand is not null)
        {
            var chevron = new TextBlock
            {
                Text = "›",
                FontSize = 16,
                Margin = new Thickness(10, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("ChromeMutedBrush"),
            };
            DockPanel.SetDock(chevron, Dock.Right);
            panel.Children.Add(chevron);
        }

        if (icon is not null)
        {
            var glyph = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(icon),
                Width = 18,
                Height = 18,
                Stretch = Stretch.Uniform,
                Fill = (Brush)FindResource("ChromeTextBrush"),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(glyph, Dock.Left);
            panel.Children.Add(glyph);
        }

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("ChromeTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        row.Content = panel;
        row.Click += (_, _) =>
        {
            if (expand is not null)
            {
                expand();
                return;
            }

            BrowserMenuPopup.IsOpen = false;
            action?.Invoke();
        };
        BrowserMenuPanel.Children.Add(row);
    }

    private void AddMenuNote(string text)
    {
        BrowserMenuPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Margin = new Thickness(44, 6, 12, 8),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("ChromeMutedBrush"),
        });
    }

    private void AddMenuSeparator()
    {
        if (BrowserMenuPanel.Children.Count == 0)
        {
            return;
        }

        BrowserMenuPanel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(12, 6, 12, 6),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
        });
    }
}
