using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VieNLink;

/// <summary>Простое модальное окно для вставки текста: ссылки, списки, заметки.</summary>
internal sealed class TextInputWindow : Window
{
    private readonly TextBox _textBox;

    private TextInputWindow(string title, string hint, string initial)
    {
        Title = title;
        Width = 680;
        Height = 380;
        MinWidth = 480;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("AppBackgroundBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var hintBlock = new TextBlock
        {
            Text = hint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        hintBlock.SetResourceReference(TextBlock.StyleProperty, "HintText");
        grid.Children.Add(hintBlock);

        _textBox = new TextBox
        {
            AcceptsReturn = true,
            Text = initial,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
        };
        _textBox.SetResourceReference(FrameworkElement.StyleProperty, "AppMultilineTextBox");
        Grid.SetRow(_textBox, 1);
        grid.Children.Add(_textBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var ok = new Button { Content = "Добавить", IsDefault = true, MinWidth = 110 };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        ok.Click += (_, _) => DialogResult = true;

        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "BaseButton");

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;
        Loaded += (_, _) => _textBox.Focus();
    }

    /// <summary>Показывает окно и возвращает введённый текст или null при отмене.</summary>
    public static string? Show(Window owner, string title, string hint, string initial = "")
    {
        var window = new TextInputWindow(title, hint, initial) { Owner = owner };
        return window.ShowDialog() == true ? window._textBox.Text : null;
    }
}

