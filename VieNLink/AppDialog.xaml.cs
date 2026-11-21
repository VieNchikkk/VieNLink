using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VieNLink;

/// <summary>Тематическое окно сообщения вместо системного MessageBox.</summary>
public partial class AppDialog : Window
{
    private readonly MessageBoxButton _buttons;
    private MessageBoxResult _result;
    private Button? _defaultButton;

    public AppDialog(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image,
        Window? owner)
    {
        InitializeComponent();
        _buttons = buttons;
        _result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;

        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "VieNLink" : title;
        MessageText.Text = message;
        ConfigureIcon(image);
        BuildButtons(buttons);

        Loaded += (_, _) =>
        {
            _defaultButton?.Focus();
            Activate();
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public MessageBoxResult Result => _result;

    private void ConfigureIcon(MessageBoxImage image)
    {
        string background;
        string foreground;
        var symbol = "i";
        switch (image)
        {
            case MessageBoxImage.Warning:
                background = "WarningSurfaceBrush";
                foreground = "WarningBrush";
                symbol = "!";
                break;
            case MessageBoxImage.Question:
                background = "AccentSurfaceBrush";
                foreground = "AccentBrush";
                symbol = "?";
                break;
            case MessageBoxImage.Error:
                background = "DangerSurfaceBrush";
                foreground = "DangerBrush";
                symbol = "✕";
                break;
            default:
                background = "AccentSurfaceBrush";
                foreground = "AccentBrush";
                break;
        }

        IconBadge.Background = (Brush)FindResource(background);
        IconText.Foreground = (Brush)FindResource(foreground);
        IconText.Text = symbol;
    }

    private void BuildButtons(MessageBoxButton buttons)
    {
        _defaultButton = null;
        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton("ОК", MessageBoxResult.OK, "PrimaryButton", true, true);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("ОК", MessageBoxResult.OK, "PrimaryButton", true, false);
                AddButton("Отмена", MessageBoxResult.Cancel, "SmallButton", false, true);
                break;
            case MessageBoxButton.YesNo:
                AddButton("Да", MessageBoxResult.Yes, "PrimaryButton", true, false);
                AddButton("Нет", MessageBoxResult.No, "SmallButton", false, true);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("Да", MessageBoxResult.Yes, "PrimaryButton", true, false);
                AddButton("Нет", MessageBoxResult.No, "SmallButton", false, false);
                AddButton("Отмена", MessageBoxResult.Cancel, "SmallButton", false, true);
                break;
        }
    }

    private Button AddButton(
        string text,
        MessageBoxResult result,
        string styleKey,
        bool isDefault,
        bool isCancel)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource(styleKey),
            MinWidth = 86,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        button.Click += (_, _) => CloseWith(result);
        ButtonsPanel.Children.Add(button);
        if (isDefault)
        {
            _defaultButton = button;
        }

        return button;
    }

    private void CloseWith(MessageBoxResult result)
    {
        _result = result;
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseWith(_buttons == MessageBoxButton.YesNo
                ? MessageBoxResult.No
                : MessageBoxResult.Cancel);
            e.Handled = true;
        }
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        CloseWith(_buttons == MessageBoxButton.YesNo
            ? MessageBoxResult.No
            : MessageBoxResult.Cancel);
    }
}
