using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VieNLink;

/// <summary>История браузера: поиск, открытие, удаление записей.</summary>
public sealed partial class BrowserHistoryWindow : Window
{
    private readonly BrowserHistory _history;
    private readonly Action<string> _openUrl;

    internal BrowserHistoryWindow(BrowserHistory history, Action<string> openUrl)
    {
        InitializeComponent();
        _history = history;
        _openUrl = openUrl;
        Refresh(string.Empty);
    }

    private void Refresh(string query)
    {
        var text = (query ?? string.Empty).Trim();
        var items = string.IsNullOrEmpty(text)
            ? _history.Items.ToList()
            : _history.Items
                .Where(entry => entry.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                entry.Url.Contains(text, StringComparison.OrdinalIgnoreCase))
                .ToList();
        HistoryList.ItemsSource = items;
        CountText.Text = items.Count == 0 ? "Ничего не найдено" : $"Записей: {items.Count}";
    }

    private BrowserHistoryEntry? Selected => HistoryList.SelectedItem as BrowserHistoryEntry;

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh(SearchBox.Text);

    private void OnOpenSelected(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (Selected is not { } entry)
        {
            return;
        }

        _openUrl(entry.Url);
        Close();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry)
        {
            return;
        }

        _history.Remove(entry);
        Refresh(SearchBox.Text);
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_history.Items.Count == 0)
        {
            return;
        }

        var answer = MessageBox.Show(this, "Очистить всю историю браузера?", "История",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _history.Clear();
        Refresh(SearchBox.Text);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
