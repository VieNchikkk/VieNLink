using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Экран источников: список с количеством узлов, включение/выключение, добавление
/// подписки или одиночной ссылки и обновление. Формат хранения — общий с Windows
/// (<c>sources.dat</c>, шифруется ключом Keystore).
/// </summary>
#if DEBUG
// В отладочной сборке экран открыт наружу: так его запускают adb-команды из §0 плана.
[Activity(Label = "Источники", Theme = "@android:style/Theme.Material.Light.NoActionBar", Exported = true)]
#else
[Activity(Label = "Источники", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
#endif
public sealed class SourcesActivity : Activity
{
    private readonly List<SourceDefinition> _rows = new();
    private readonly List<string> _labels = new();
    private ArrayAdapter<string>? _adapter;
    private ListView? _list;
    private TextView? _summary;
    private TextView? _progress;
    private bool _busy;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildUi();
        _ = LoadAsync();
    }

    private void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        root.SetPadding(
            UiMetrics.Dp(this, 16),
            UiMetrics.StatusBar(this) + UiMetrics.Dp(this, 8),
            UiMetrics.Dp(this, 16),
            UiMetrics.NavigationBar(this) + UiMetrics.Dp(this, 8));

        var title = new TextView(this) { Text = "Источники", TextSize = 24f };
        title.SetTypeface(null, TypefaceStyle.Bold);
        root.AddView(title);

        _summary = new TextView(this) { TextSize = 16f };
        _summary.SetTypeface(null, TypefaceStyle.Bold);
        _progress = new TextView(this) { TextSize = 13f };
        _progress.SetTextColor(Color.DarkGray);

        var actions = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        var add = new Button(this) { Text = "Добавить", TextSize = 13f };
        add.Click += (_, _) => ShowAddDialog();
        var refresh = new Button(this) { Text = "Обновить все", TextSize = 13f };
        refresh.Click += async (_, _) => await RunRefreshAsync();
        actions.AddView(add, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(refresh, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var list = new ListView(this);
        _list = list;
        list.ItemClick += (_, args) => ToggleSource(args.Position);
        list.ItemLongClick += (_, args) => ConfirmRemove(args.Position);

        var hint = new TextView(this)
        {
            Text = "Короткий тап — включить или выключить. Долгий — удалить.",
            TextSize = 12f,
        };
        hint.SetTextColor(Color.Gray);

        root.AddView(_summary);
        root.AddView(_progress);
        root.AddView(actions);
        root.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        root.AddView(hint);

        SetContentView(root);
    }

    private async Task LoadAsync()
    {
        try
        {
            await Task.Run(() => NodeStore.EnsureLoaded());
        }
        catch (Exception exception)
        {
            AppLog.Write("sources", "Не удалось прочитать источники", exception);
            SetProgress("Ошибка чтения: " + exception.Message);
        }

        Reload();
    }

    private void Reload()
    {
        _rows.Clear();
        _rows.AddRange(NodeStore.SnapshotSources());

        _labels.Clear();
        foreach (var source in _rows)
        {
            var mark = source.Enabled ? "☑" : "☐";
            var count = source.LastNodeCount > 0 ? source.LastNodeCount + " узлов" : "—";
            var status = source.LastError is not null
                ? "ошибка: " + source.LastError
                : source.LastCheckedUtc is null
                    ? "не проверялся"
                    : "обновлён " + source.LastCheckedUtc.Value.ToLocalTime().ToString("HH:mm");
            var mine = source.IsMine ? " ★" : string.Empty;

            _labels.Add($"{mark} {source.Name}{mine}\n{count} · {status}");
        }

        // ArrayAdapter создаётся заново: с пустым списком он не подхватывает добавленные строки.
        if (_list is not null)
        {
            _adapter = new ArrayAdapter<string>(
                this,
                Android.Resource.Layout.SimpleListItem1,
                _labels.ToList());
            _list.Adapter = _adapter;
        }

        if (_summary is not null)
        {
            var enabled = _rows.Count(source => source.Enabled);
            _summary.Text = $"Источников: {_rows.Count} · включено: {enabled}";
        }
    }

    private void ToggleSource(int position)
    {
        if (position < 0 || position >= _rows.Count)
        {
            return;
        }

        var source = _rows[position];
        NodeStore.SetSourceEnabled(source, !source.Enabled);
        Reload();
        SetProgress((source.Enabled ? "Включён: " : "Выключен: ") + source.Name);
    }

    private void ConfirmRemove(int position)
    {
        if (position < 0 || position >= _rows.Count)
        {
            return;
        }

        var source = _rows[position];
        using var dialog = new AlertDialog.Builder(this)
            .SetMessage("Удалить источник «" + source.Name + "»?")
            .SetPositiveButton("Удалить", (_, _) =>
            {
                NodeStore.RemoveSource(source);
                Reload();
                SetProgress("Источник удалён: " + source.Name);
            })
            .SetNegativeButton("Отмена", (_, _) => { })
            .Create();

        dialog.Show();
    }

    private void ShowAddDialog()
    {
        var layout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        layout.SetPadding(Dp(20), Dp(10), Dp(20), Dp(10));

        var url = new EditText(this) { Hint = "https://…/sub или vless://…" };
        var name = new EditText(this) { Hint = "Название (необязательно)" };
        var mine = new CheckBox(this) { Text = "Моя подписка" };

        layout.AddView(url);
        layout.AddView(name);
        layout.AddView(mine);

        using var dialog = new AlertDialog.Builder(this)
            .SetTitle("Добавить источник")
            .SetView(layout)
            .SetPositiveButton("Добавить", (_, _) => _ = AddSourceAsync(url.Text, name.Text, mine.Checked))
            .SetNegativeButton("Отмена", (_, _) => { })
            .Create();

        dialog.Show();
    }

    private async Task AddSourceAsync(string? input, string? name, bool mine)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var before = NodeStore.SnapshotSources();
            var message = await Task.Run(() => NodeStore.AddSource(input ?? string.Empty, name, mine));
            SetProgress(message);
            Reload();

            var added = NodeStore.SnapshotSources().FirstOrDefault(source => !before.Contains(source));
            if (added is null)
            {
                return;
            }

            SetProgress("Загружаю: " + added.Name);
            var progress = new Progress<string>(SetProgress);
            var result = await NodeStore.ImportSourceAsync(added, progress, CancellationToken.None);
            SetProgress(result);
        }
        catch (Exception exception)
        {
            AppLog.Write("sources", "Не удалось добавить источник", exception);
            SetProgress("Ошибка добавления: " + exception.Message);
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    private async Task RunRefreshAsync()
    {
        if (_busy)
        {
            SetProgress("Дождитесь окончания загрузки");
            return;
        }

        _busy = true;
        SetProgress("Обновляю источники…");
        try
        {
            var progress = new Progress<string>(SetProgress);
            var summary = await NodeStore.RefreshAsync(progress, CancellationToken.None);
            SetProgress(summary);
        }
        catch (Exception exception)
        {
            AppLog.Write("sources", "Ошибка обновления источников", exception);
            SetProgress("Ошибка обновления: " + exception.Message);
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    private void SetProgress(string text) => RunOnUiThread(() =>
    {
        if (_progress is not null)
        {
            _progress.Text = text;
        }
    });

    private int Dp(int value) => (int)Math.Round(value * Resources!.DisplayMetrics!.Density);
}
