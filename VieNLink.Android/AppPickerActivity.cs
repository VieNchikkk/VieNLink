using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Выбор приложений для split-tunnel: только отмеченные пойдут через VPN.
/// Список берётся у <see cref="PackageManager"/> (приложения с иконкой в лаунчере),
/// выбор сохраняется сразу в <c>android-settings.json</c>.
/// </summary>
#if DEBUG
// В отладочной сборке экран открыт наружу: так его запускают adb-команды из §0 плана.
[Activity(Label = "Приложения", Theme = "@android:style/Theme.Material.Light.NoActionBar", Exported = true)]
#else
[Activity(Label = "Приложения", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
#endif
public sealed class AppPickerActivity : Activity
{
    private readonly List<AppEntry> _all = new();
    private readonly List<AppEntry> _shown = new();
    private AppAdapter? _adapter;
    private TextView? _summary;
    private EditText? _filter;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        root.SetPadding(
            UiMetrics.Dp(this, 16),
            UiMetrics.StatusBar(this) + UiMetrics.Dp(this, 8),
            UiMetrics.Dp(this, 16),
            UiMetrics.NavigationBar(this) + UiMetrics.Dp(this, 8));

        var title = new TextView(this) { Text = "Приложения через VPN", TextSize = 24f };
        title.SetTypeface(null, TypefaceStyle.Bold);

        _summary = new TextView(this) { TextSize = 14f };
        _summary.SetTextColor(Color.DarkGray);

        _filter = new EditText(this) { Hint = "Фильтр по названию" };
        _filter.TextChanged += (_, _) => ApplyFilter(_filter.Text);

        var list = new ListView(this);
        _adapter = new AppAdapter(this, _shown);
        list.Adapter = _adapter;
        list.ItemClick += (_, args) => Toggle(args.Position);

        root.AddView(title);
        root.AddView(_summary);
        root.AddView(_filter);
        root.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        SetContentView(root);

        LoadApps();
    }

    private void LoadApps()
    {
        try
        {
            var manager = PackageManager!;
            var launcher = new Intent(Intent.ActionMain).AddCategory(Intent.CategoryLauncher);
            var resolved = manager.QueryIntentActivities(launcher, 0);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var info in resolved)
            {
                var package = info.ActivityInfo?.PackageName;
                if (string.IsNullOrEmpty(package) || !seen.Add(package))
                {
                    continue;
                }

                var label = info.LoadLabel(manager)?.ToString() ?? package;
                _all.Add(new AppEntry(package, label));
            }

            _all.Sort((left, right) =>
                string.Compare(left.Label, right.Label, StringComparison.CurrentCultureIgnoreCase));
        }
        catch (Exception exception)
        {
            AppLog.Write("android", "Не удалось получить список приложений", exception);
        }

        ApplyFilter(string.Empty);
    }

    private void ApplyFilter(string? text)
    {
        var query = text?.Trim() ?? string.Empty;
        _shown.Clear();
        foreach (var entry in _all)
        {
            if (query.Length == 0 ||
                entry.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                entry.Package.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _shown.Add(entry);
            }
        }

        _adapter?.NotifyDataSetChanged();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_summary is null)
        {
            return;
        }

        var count = AndroidSettingsStore.Current.AllowedPackages.Count;
        _summary.Text = $"Выбрано: {count} · всего в списке: {_all.Count}. " +
                        "Через VPN пойдут только отмеченные.";
    }

    private void Toggle(int position)
    {
        if (position < 0 || position >= _shown.Count)
        {
            return;
        }

        var entry = _shown[position];
        var allowed = !AndroidSettingsStore.IsAllowed(entry.Package);
        AndroidSettingsStore.SetAllowed(entry.Package, allowed);
        _adapter?.NotifyDataSetChanged();
        UpdateSummary();
    }

    private sealed record AppEntry(string Package, string Label);

    private sealed class AppAdapter : BaseAdapter<AppEntry>
    {
        private readonly Activity _activity;
        private readonly List<AppEntry> _items;

        public AppAdapter(Activity activity, List<AppEntry> items)
        {
            _activity = activity;
            _items = items;
        }

        public override int Count => _items.Count;

        public override AppEntry this[int position] => _items[position];

        public override long GetItemId(int position) => position;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var entry = _items[position];
            var check = new CheckBox(_activity)
            {
                Text = entry.Label + "\n" + entry.Package,
                TextSize = 15f,
                Checked = AndroidSettingsStore.IsAllowed(entry.Package),
                Clickable = false,
                Focusable = false,
            };
            check.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));
            return check;
        }

        private int Dp(int value) =>
            (int)Math.Round(value * _activity.Resources!.DisplayMetrics!.Density);
    }
}
