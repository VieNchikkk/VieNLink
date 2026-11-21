using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Журнал приложения и лог ядра (stderr Go). Нужен, чтобы разбирать сбои на телефоне
/// без подключения к компьютеру.
/// </summary>
#if DEBUG
[Activity(Label = "Журнал", Theme = "@android:style/Theme.Material.Light.NoActionBar", Exported = true)]
#else
[Activity(Label = "Журнал", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
#endif
public sealed class LogActivity : Activity
{
    private const int MaxLines = 400;

    private TextView? _view;
    private TextView? _info;
    private CheckBox? _coreLog;
    private ScrollView? _scroll;
    private bool _loading;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        root.SetPadding(
            UiMetrics.Dp(this, 16),
            UiMetrics.StatusBar(this) + UiMetrics.Dp(this, 8),
            UiMetrics.Dp(this, 16),
            UiMetrics.NavigationBar(this) + UiMetrics.Dp(this, 8));

        var title = new TextView(this) { Text = "Журнал", TextSize = 24f };
        title.SetTypeface(null, TypefaceStyle.Bold);

        _info = new TextView(this) { TextSize = 12f };
        _info.SetTextColor(Color.DarkGray);

        var actions = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        var refresh = new Button(this) { Text = "Обновить", TextSize = 13f };
        refresh.Click += async (_, _) => await LoadAsync();
        var share = new Button(this) { Text = "Поделиться", TextSize = 13f };
        share.Click += (_, _) => Share();
        actions.AddView(refresh, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(share, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        _coreLog = new CheckBox(this) { Text = "Лог ядра (stderr)", TextSize = 14f };
        _coreLog.CheckedChange += async (_, _) => await LoadAsync();

        _view = new TextView(this)
        {
            TextSize = 11f,
            Typeface = Typeface.Monospace,
        };
        _view.SetTextIsSelectable(true);

        var scroll = new ScrollView(this);
        scroll.AddView(_view);
        _scroll = scroll;

        root.AddView(title);
        root.AddView(_info);
        root.AddView(actions);
        root.AddView(_coreLog);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        SetContentView(root);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            var core = _coreLog?.Checked == true;
            var (text, source) = await Task.Run(() => Read(core));
            if (_view is not null)
            {
                _view.Text = text;
                // Свежие записи в конце — показываем их, а не начало файла.
                _scroll?.Post(() => _scroll.FullScroll(FocusSearchDirection.Down));
            }

            if (_info is not null)
            {
                _info.Text = source;
            }
        }
        catch (Exception exception)
        {
            if (_view is not null)
            {
                _view.Text = "Не удалось прочитать журнал: " + exception.Message;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private (string Text, string Source) Read(bool core)
    {
        if (core)
        {
            var path = System.IO.Path.Combine(FilesDir!.AbsolutePath, "core-stderr.log");
            return (Tail(path, MaxLines, "Лог ядра пуст: ядро ещё не писало в stderr."),
                "Ядро: " + path);
        }

        var directory = AppPaths.LogsDirectory;
        var latest = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "vienlink-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        if (latest is null)
        {
            return ("Журнал приложения пуст.", "Файл ещё не создан");
        }

        return (Tail(latest, MaxLines, "Журнал пуст."), "Приложение: " + latest);
    }

    private static string Tail(string path, int count, string empty)
    {
        if (!File.Exists(path))
        {
            return empty;
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
        {
            return empty;
        }

        var start = Math.Max(0, lines.Length - count);
        return string.Join(System.Environment.NewLine, lines[start..]);
    }

    private void Share()
    {
        try
        {
            var send = new Intent(Intent.ActionSend)!
                .SetType("text/plain")!
                .PutExtra(Intent.ExtraText, _view?.Text ?? string.Empty)!
                .PutExtra(Intent.ExtraSubject, "VieNLink — журнал")!;
            StartActivity(Intent.CreateChooser(send, "Поделиться журналом"));
        }
        catch (Exception exception)
        {
            Toast.MakeText(this, "Не удалось поделиться: " + exception.Message, ToastLength.Short)?.Show();
        }
    }
}
