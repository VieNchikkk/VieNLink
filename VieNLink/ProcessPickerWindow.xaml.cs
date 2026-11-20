using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VieNLink;

/// <summary>Строка списка процессов для выбора приложений.</summary>
public sealed record ProcessEntry(string Name, string Path);

/// <summary>
/// Выбор приложений из числа запущенных процессов: удобнее, чем вписывать имена вручную.
/// </summary>
public partial class ProcessPickerWindow : Window
{
    private readonly List<ProcessEntry> _all = new();

    public ProcessPickerWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Имена выбранных процессов (например, chrome.exe).</summary>
    public IReadOnlyList<string> SelectedNames { get; private set; } = Array.Empty<string>();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadProcesses();
        ApplyFilter();
        SearchBox.Focus();
    }

    private void LoadProcesses()
    {
        var byName = new Dictionary<string, ProcessEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName + ".exe";
                var path = string.Empty;
                try
                {
                    path = process.MainModule?.FileName ?? string.Empty;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                }

                if (!byName.TryGetValue(name, out var existing) ||
                    (existing.Path.Length == 0 && path.Length > 0))
                {
                    byName[name] = new ProcessEntry(name, path);
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        _all.Clear();
        _all.AddRange(byName.Values.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase));
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var items = string.IsNullOrEmpty(query)
            ? _all
            : _all.Where(entry =>
                    entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    entry.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        ProcessList.ItemsSource = items;
        CountText.Text = $"Найдено: {items.Count} из {_all.Count}";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnAddClick(object sender, RoutedEventArgs e) => AddSelected();

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => AddSelected();

    private void AddSelected()
    {
        var names = ProcessList.SelectedItems
            .OfType<ProcessEntry>()
            .Select(entry => entry.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
        {
            return;
        }

        SelectedNames = names;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
