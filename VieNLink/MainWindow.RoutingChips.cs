using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VieNLink;

public partial class MainWindow
{
    // ---------- плитки сайтов и приложений в «Туннелировании» ----------

    private void RefreshRoutingChips()
    {
        if (SiteChipsGrid is null || AppChipsGrid is null)
        {
            return;
        }

        RefreshSiteChips();
        RefreshAppChips();
    }

    private void RefreshSiteChips()
    {
        SiteChipsGrid.Children.Clear();
        var sites = ParseSiteValues();
        if (sites.Count == 0)
        {
            SiteChipsGrid.Children.Add(EmptyChip("Пока пусто — добавьте сайт ниже"));
            return;
        }

        foreach (var site in sites)
        {
            SiteChipsGrid.Children.Add(BuildChip(site, SiteIcon(site), () => RemoveSite(site)));
        }
    }

    private void RefreshAppChips()
    {
        AppChipsGrid.Children.Clear();
        var apps = ParseAppValues();
        if (apps.Count == 0)
        {
            AppChipsGrid.Children.Add(EmptyChip("Пока пусто — выберите приложение"));
            return;
        }

        foreach (var app in apps)
        {
            AppChipsGrid.Children.Add(BuildChip(app, ("▣", "AccentBrush"), () => RemoveApp(app)));
        }
    }

    private void OnAddSiteChipClick(object sender, RoutedEventArgs e)
    {
        AddSiteValues(NewSiteBox.Text);
        NewSiteBox.Clear();
    }

    private void OnAddAppChipClick(object sender, RoutedEventArgs e)
    {
        AddAppValues(NewAppBox.Text);
        NewAppBox.Clear();
    }

    private void AddSiteValues(string? text)
    {
        var values = ParseSiteValues();
        foreach (var raw in SplitValues(text))
        {
            var value = raw.Trim();
            if (value.Contains("://", StringComparison.Ordinal) &&
                Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                value = uri.Host;
            }

            var slash = value.IndexOf('/');
            if (slash >= 0)
            {
                value = value[..slash];
            }

            value = value.TrimStart('*').TrimStart('.').ToLowerInvariant();
            if (value.Length == 0 || value.Contains(' '))
            {
                continue;
            }

            if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(value);
            }
        }

        SiteListBox.Text = string.Join(Environment.NewLine, values);
        RefreshSiteChips();
    }

    private void AddAppValues(string? text)
    {
        var values = ParseAppValues();
        foreach (var raw in SplitValues(text))
        {
            var value = raw.Trim().Replace('/', '\\');
            var slash = value.LastIndexOf('\\');
            if (slash >= 0)
            {
                value = value[(slash + 1)..];
            }

            if (value.Length == 0)
            {
                continue;
            }

            if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                value += ".exe";
            }

            if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(value);
            }
        }

        AppListBox.Text = string.Join(Environment.NewLine, values);
        RefreshAppChips();
    }

    private void RemoveSite(string site)
    {
        var values = ParseSiteValues();
        values.RemoveAll(value => value.Equals(site, StringComparison.OrdinalIgnoreCase));
        SiteListBox.Text = string.Join(Environment.NewLine, values);
        RefreshSiteChips();
    }

    private void RemoveApp(string app)
    {
        var values = ParseAppValues();
        values.RemoveAll(value => value.Equals(app, StringComparison.OrdinalIgnoreCase));
        AppListBox.Text = string.Join(Environment.NewLine, values);
        RefreshAppChips();
    }

    private List<string> ParseSiteValues() =>
        SplitValues(SiteListBox.Text)
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private List<string> ParseAppValues() =>
        SplitValues(AppListBox.Text)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IEnumerable<string> SplitValues(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split(new[] { '\r', '\n', ',', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (string Glyph, string BrushKey) SiteIcon(string site)
    {
        if (site.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
            site.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            site.Contains("googlevideo", StringComparison.OrdinalIgnoreCase) ||
            site.Contains("ytimg", StringComparison.OrdinalIgnoreCase))
        {
            return ("▶", "YouTubeBrush");
        }

        if (site.Contains("discord", StringComparison.OrdinalIgnoreCase))
        {
            return ("D", "DiscordBrush");
        }

        if (site.Contains("telegram", StringComparison.OrdinalIgnoreCase) ||
            site.Equals("t.me", StringComparison.OrdinalIgnoreCase) ||
            site.EndsWith(".t.me", StringComparison.OrdinalIgnoreCase))
        {
            return ("✈", "TelegramBrush");
        }

        return ("◍", "AccentBrush");
    }

    private FrameworkElement BuildChip(string label, (string Glyph, string BrushKey) icon, Action remove)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            BorderBrush = (Brush)FindResource("TableGridLineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 4, 4, 4),
            Margin = new Thickness(0, 0, 8, 8),
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource(icon.BrushKey),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
            Child = new TextBlock
            {
                Text = icon.Glyph,
                Foreground = Brushes.White,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = label,
        };

        var removeButton = new Button
        {
            Content = "✕",
            Style = (Style)FindResource("SmallButton"),
            Padding = new Thickness(4, 0, 4, 0),
            FontSize = 10,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Убрать из списка",
        };
        removeButton.Click += (_, _) => remove();

        Grid.SetColumn(badge, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(removeButton, 2);
        row.Children.Add(badge);
        row.Children.Add(text);
        row.Children.Add(removeButton);
        border.Child = row;
        return border;
    }

    private FrameworkElement EmptyChip(string text) => new TextBlock
    {
        Text = text,
        Foreground = (Brush)FindResource("MutedStrongBrush"),
        FontStyle = FontStyles.Italic,
        Margin = new Thickness(2, 4, 0, 4),
    };
}
