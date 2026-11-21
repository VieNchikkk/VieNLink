using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
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
[Activity(Label = "Приложения", Theme = "@style/Theme.VieNLink", Exported = true)]
#else
[Activity(Label = "Приложения", Theme = "@style/Theme.VieNLink")]
#endif
public sealed class AppPickerActivity : BaseActivity
{
    private readonly List<AppEntry> _all = new();
    private readonly List<AppEntry> _shown = new();
    private AppAdapter? _adapter;
    private TextView? _summary;
    private EditText? _filter;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var page = Ui.Column(this);
        page.SetPadding(0, 0, 0, 0);

        var top = Ui.Row(this);
        top.AddView(Ui.Pill(this, P, "Назад", UiButtonKind.Ghost, Finish));
        var title = Ui.Title(this, "Приложения через VPN", P);
        title.SetPadding(Ui.Dp(this, 10), 0, 0, 0);
        top.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(top);

        _summary = Ui.Hint(this, "Читаю список…", P);
        _summary.SetPadding(0, Ui.Dp(this, 8), 0, 0);
        page.AddView(_summary);

        _filter = Ui.Input(this, P, "Фильтр по названию или пакету");
        _filter.TextChanged += (_, _) => ApplyFilter(_filter.Text);
        Ui.Add(page, _filter, 8);

        var list = new ListView(this);
        list.DividerHeight = 0;
        _adapter = new AppAdapter(this, _shown, P);
        list.Adapter = _adapter;
        list.ItemClick += (_, args) => Toggle(args.Position);
        page.AddView(list, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var hint = Ui.Hint(this,
            "Короткий тап отмечает приложение. Режим выбирается на экране «Туннелирование»: " +
            "«только выбранные приложения» или «все, кроме выбранных».", P);
        hint.SetPadding(0, Ui.Dp(this, 8), 0, 0);
        page.AddView(hint);

        SetPage(page, scroll: false);
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
                Drawable? icon = null;
                try
                {
                    icon = info.LoadIcon(manager)?.Mutate();
                }
                catch (Exception exception)
                {
                    // Битая иконка не должна ломать список.
                    Android.Util.Log.Warn("VieNLink", "icon " + package + ": " + exception.Message);
                }

                _all.Add(new AppEntry(package, label, icon));
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

        var settings = AndroidSettingsStore.Current;
        var count = settings.AllowedPackages.Count;
        var mode = settings.AppRouteMode switch
        {
            AppRouteMode.OnlyListed => "через VPN пойдут только отмеченные",
            AppRouteMode.AllExceptListed => "через VPN пойдут все, кроме отмеченных",
            _ => "выбран режим «все приложения» — отметки не влияют",
        };
        _summary.Text = $"Выбрано: {count} · всего приложений: {_all.Count}. Сейчас {mode}.";
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

    private sealed record AppEntry(string Package, string Label, Drawable? Icon);

    private sealed class AppAdapter : BaseAdapter<AppEntry>
    {
        private readonly Activity _activity;
        private readonly List<AppEntry> _items;
        private readonly Palette _palette;

        public AppAdapter(Activity activity, List<AppEntry> items, Palette palette)
        {
            _activity = activity;
            _items = items;
            _palette = palette;
        }

        public override int Count => _items.Count;

        public override AppEntry this[int position] => _items[position];

        public override long GetItemId(int position) => position;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var entry = _items[position];
            var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
            row.SetPadding(Ui.Dp(_activity, 12), Ui.Dp(_activity, 10),
                Ui.Dp(_activity, 12), Ui.Dp(_activity, 10));
            row.Background = Ui.Rounded(_palette.Card, 12, _activity, _palette.Border);

            var icon = new ImageView(_activity);
            icon.SetScaleType(ImageView.ScaleType.FitCenter);
            if (entry.Icon is not null)
            {
                icon.SetImageDrawable(entry.Icon);
            }
            else
            {
                // Заглушка не нужна: без иконки остаётся только текстовый блок.
                icon.Visibility = ViewStates.Gone;
            }

            var iconLayout = new LinearLayout.LayoutParams(Ui.Dp(_activity, 38), Ui.Dp(_activity, 38));
            iconLayout.RightMargin = Ui.Dp(_activity, 10);
            row.AddView(icon, iconLayout);

            var texts = Ui.Column(_activity);
            texts.AddView(Ui.Text(_activity, entry.Label, 14f, _palette.TextStrong, bold: true));
            texts.AddView(Ui.Text(_activity, entry.Package, 10.5f, _palette.Muted));
            row.AddView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            var check = new CheckBox(_activity)
            {
                Checked = AndroidSettingsStore.IsAllowed(entry.Package),
                Clickable = false,
                Focusable = false,
            };
            check.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(_palette.Accent);
            row.AddView(check);

            var wrapper = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
            wrapper.SetPadding(0, Ui.Dp(_activity, 3), 0, Ui.Dp(_activity, 3));
            wrapper.AddView(row, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            return wrapper;
        }
    }
}
