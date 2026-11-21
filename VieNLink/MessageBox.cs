using System.Windows;

namespace VieNLink;

/// <summary>
/// Заменяет системный MessageBox на тематическое окно приложения.
/// Имя совпадает с System.Windows.MessageBox, поэтому все прежние вызовы
/// в namespace VieNLink автоматически используют этот класс.
/// </summary>
internal static class MessageBox
{
    public static MessageBoxResult Show(string messageBoxText) =>
        Show(null, messageBoxText, "VieNLink", MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string messageBoxText, string caption) =>
        Show(null, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button) =>
        Show(null, messageBoxText, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon) =>
        Show(null, messageBoxText, caption, button, icon);

    public static MessageBoxResult Show(
        Window? owner,
        string messageBoxText,
        string caption) =>
        Show(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button) =>
        Show(owner, messageBoxText, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
    {
        owner ??= Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive && window.IsVisible);
        if (owner is { IsVisible: false })
        {
            owner = null;
        }

        var dialog = new AppDialog(messageBoxText, caption, button, icon, owner);
        dialog.ShowDialog();
        return dialog.Result;
    }
}
