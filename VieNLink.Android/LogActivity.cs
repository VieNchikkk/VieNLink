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
[Activity(Label = "Журнал", Theme = "@style/Theme.VieNLink", Exported = true)]
#else
[Activity(Label = "Журнал", Theme = "@style/Theme.VieNLink")]
#endif
public sealed class LogActivity : BaseActivity
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

        var page = ColumnPage();
        var top = Ui.Row(this);
        top.AddView(Ui.Pill(this, P, "Назад", UiButtonKind.Ghost, Finish));
        var title = Ui.Title(this, "Журнал", P);
        title.SetPadding(Ui.Dp(this, 10), 0, 0, 0);
        top.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(top);

        _info = Ui.Hint(this, "Читаю…", P);
        _info.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        page.AddView(_info);

        var actions = Ui.Row(this);
        var refresh = Ui.Pill(this, P, "Обновить", UiButtonKind.Accent,
            () => _ = LoadAsync(), Resource.Drawable.ic_refresh);
        var share = Ui.Pill(this, P, "Поделиться", UiButtonKind.Accent,
            Share, Resource.Drawable.ic_export);
        actions.AddView(refresh, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        actions.AddView(share, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        Ui.Add(page, actions, 10);

        var clear = Ui.Pill(this, P, "Очистить сегодняшний журнал", UiButtonKind.Ghost, () =>
        {
            if (LogReader.Clear())
            {
                Ui.Toast(this, "Журнал очищен");
                _ = LoadAsync();
            }
        }, Resource.Drawable.ic_delete);
        Ui.Add(page, clear, 8);

        _coreLog = new CheckBox(this) { Text = "Лог ядра (stderr)" };
        _coreLog.SetTextColor(P.TextSoft());
        _coreLog.CheckedChange += async (_, _) => await LoadAsync();
        Ui.Add(page, _coreLog, 8);

        _view = new TextView(this)
        {
            TextSize = 11f,
            Typeface = Typeface.Monospace,
        };
        _view.SetTextColor(P.MutedStrong);
        _view.SetTextIsSelectable(true);
        _view.SetBackgroundColor(P.Inner);
        _view.SetPadding(Ui.Dp(this, 10), Ui.Dp(this, 10), Ui.Dp(this, 10), Ui.Dp(this, 10));

        var scroll = new ScrollView(this) { VerticalScrollBarEnabled = true };
        scroll.AddView(_view);
        _scroll = scroll;

        var scrollLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Ui.Dp(this, 460));
        scrollLayout.TopMargin = Ui.Dp(this, 10);
        page.AddView(scroll, scrollLayout);

        var hint = Ui.Hint(this, "Хранятся последние 400 строк. Журналы ротируются по 2 МБ и живут 14 дней.", P);
        Ui.Add(page, hint, 8);

        SetPage(page);
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
            var (text, source) = await Task.Run(() => LogReader.Read(core, MaxLines));
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

    private void Share()
    {
        try
        {
            var send = new Intent(Intent.ActionSend)
                .SetType("text/plain")
                .PutExtra(Intent.ExtraText, _view?.Text ?? string.Empty)
                .PutExtra(Intent.ExtraSubject, "VieNLink — журнал");
            StartActivity(Intent.CreateChooser(send, "Поделиться журналом"));
        }
        catch (Exception exception)
        {
            Ui.Toast(this, "Не удалось поделиться: " + exception.Message);
        }
    }
}
