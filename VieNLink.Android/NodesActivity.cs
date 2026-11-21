using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Экран узлов без списка: сводка (всего, рабочих, доступ по сервисам), обновление
/// и проверка в отдельном процессе. Подключение — с главного экрана и из шапки.
/// </summary>
#if DEBUG
[Activity(Label = "Узлы", Theme = "@style/Theme.VieNLink", Exported = true)]
#else
[Activity(Label = "Узлы", Theme = "@style/Theme.VieNLink")]
#endif
public sealed class NodesActivity : BaseActivity
{
    private TextView? _summary;
    private TextView? _detail;
    private TextView? _progress;
    private ProgressBar? _bar;

    private CancellationTokenSource? _work;
    private bool _busy;

    private bool _autoRefresh;
    private bool _autoTest;
    private string? _autoSelect;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _autoRefresh = Intent?.GetBooleanExtra("refresh", false) ?? false;
        _autoTest = Intent?.GetBooleanExtra("test", false) ?? false;
        _autoSelect = Intent?.GetStringExtra("selectTag");

        BuildUi();
        _ = LoadAsync();
    }

    protected override void OnResume()
    {
        base.OnResume();
        UpdateSummary();
    }

    protected override void OnDestroy()
    {
        _work?.Cancel();
        base.OnDestroy();
    }

    private void BuildUi()
    {
        var page = ColumnPage();

        var top = Ui.Row(this);
        top.AddView(Ui.Pill(this, P, "Назад", UiButtonKind.Ghost, Finish));
        var title = Ui.Title(this, "Узлы", P);
        title.SetPadding(Ui.Dp(this, 10), 0, 0, 0);
        top.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        top.AddView(Ui.Badge(this, P, "СПИСОК СКРЫТ", P.Accent));
        page.AddView(top);

        var card = Ui.HeaderCard(P, this);
        _summary = Ui.Text(this, "Читаю кэш…", 18f, P.TextStrong, bold: true);
        card.AddView(_summary);
        _detail = Ui.Hint(this, string.Empty, P);
        _detail.SetPadding(0, Ui.Dp(this, 8), 0, 0);
        card.AddView(_detail);
        page.AddView(card);

        _progress = Ui.Hint(this, "Готово", P);
        _progress.SetPadding(0, Ui.Dp(this, 10), 0, 0);
        page.AddView(_progress);

        _bar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal)
        {
            Visibility = ViewStates.Gone,
            Indeterminate = false,
            Max = 1,
            Progress = 0,
        };
        var barLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Ui.Dp(this, 8));
        barLayout.TopMargin = Ui.Dp(this, 6);
        page.AddView(_bar, barLayout);

        var actions = Ui.Row(this);
        var refresh = Ui.Pill(this, P, "Обновить", UiButtonKind.Accent,
            () => _ = RunRefreshAsync(), Resource.Drawable.ic_refresh);
        var test = Ui.Pill(this, P, "Проверить", UiButtonKind.Accent,
            () => _ = RunTestAsync(), Resource.Drawable.ic_check);
        actions.AddView(refresh, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        actions.AddView(test, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        Ui.Add(page, actions, 12);

        var quick = Ui.Row(this);
        var measure = Ui.Pill(this, P, "Замер скорости", UiButtonKind.Ghost,
            () => _ = MeasureAsync(), Resource.Drawable.ic_speed);
        var export = Ui.Pill(this, P, "Экспорт", UiButtonKind.Ghost,
            ExportSelected, Resource.Drawable.ic_export);
        quick.AddView(measure, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        quick.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        quick.AddView(export, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        Ui.Add(page, quick, 8);

        page.AddView(Ui.Section(this, "Как выбирается узел", P));
        var how = Ui.Card(P, this,
            Bullet("Сначала личные подписки, затем маскированные (REALITY, Vision, WS/TLS), затем быстрые."),
            Bullet("Рабочий узел — тот, через который реально открылся хотя бы один проверочный сайт."),
            Bullet("Лучший открывает больше заблокированных доменов; при равенстве — меньшая задержка."),
            Bullet("Узел с 5 провалами подряд удаляется; успешная проверка обнуляет счётчик."));
        page.AddView(how);

        var hint = Ui.Banner(P, this, P.Accent, "Почему списка нет",
            "В Windows таблица узлов скрыта так же: имена серверов не показываются. Приложение берёт " +
            "проверенный узел по доступу к сервису и задержке, а вы видите только результат.");
        Ui.Add(page, hint, 12);

        SetPage(page);
    }

    private TextView Bullet(string text)
    {
        var view = Ui.Text(this, "•  " + text, 12.5f, P.MutedStrong);
        view.SetLineSpacing(0, 1.15f);
        view.SetPadding(0, Ui.Dp(this, 3), 0, Ui.Dp(this, 3));
        return view;
    }

    private async Task LoadAsync()
    {
        try
        {
            await Task.Run(() => NodeStore.EnsureLoaded());
        }
        catch (Exception exception)
        {
            AppLog.Write("nodes", "Не удалось прочитать узлы", exception);
            SetProgress("Ошибка чтения узлов: " + exception.Message);
        }

        ApplyAutoSelect();
        UpdateSummary();

        if (_autoRefresh)
        {
            await RunRefreshAsync();
        }

        if (_autoTest || _autoRefresh)
        {
            await RunTestAsync();
        }
    }

    private void ApplyAutoSelect()
    {
        if (string.IsNullOrWhiteSpace(_autoSelect))
        {
            return;
        }

        var wanted = NodeStore.Snapshot().FirstOrDefault(node =>
            node.Tag.Equals(_autoSelect, StringComparison.OrdinalIgnoreCase) ||
            node.Id.Equals(_autoSelect, StringComparison.OrdinalIgnoreCase));
        if (wanted is not null)
        {
            NodeStore.Select(wanted);
        }

        _autoSelect = null;
    }

    private void UpdateSummary()
    {
        var nodes = NodeStore.Snapshot();
        var working = nodes.Count(node => node.Status == NodeStatus.Working);
        var mine = nodes.Count(node => node.IsMine);
        var universal = nodes.Count(node => node.UniversalOk == true || node.DelayMs is not null);
        var youtube = nodes.Count(node => node.YoutubeOk == true);
        var telegram = nodes.Count(node => node.TelegramOk == true);
        var discord = nodes.Count(node => node.DiscordOk == true);
        var bestDomains = working == 0
            ? 0
            : nodes.Where(node => node.Status == NodeStatus.Working).Max(node => node.OpenDomains);

        var lastChecked = nodes
            .Where(node => node.CheckedAtUtc is not null)
            .Select(node => node.CheckedAtUtc!.Value)
            .DefaultIfEmpty()
            .Max();

        if (_summary is not null)
        {
            _summary.Text = $"{nodes.Count} узлов · рабочих {working}";
        }

        if (_detail is not null)
        {
            var checkedText = lastChecked == default
                ? "проверок ещё не было"
                : "проверено " + lastChecked.ToLocalTime().ToString("dd.MM HH:mm");
            _detail.Text = $"Доступ: универсальный {universal}, YouTube {youtube}, Telegram {telegram}, Discord {discord}\n" +
                           $"Мои подписки: {mine} · лимит {NodeStore.Settings.MaxTotalNodes} · " +
                           $"макс. доменов {bestDomains} · {checkedText}";
        }
    }

    private async Task RunRefreshAsync()
    {
        if (_busy)
        {
            Ui.Toast(this, "Дождитесь окончания операции");
            return;
        }

        _busy = true;
        _work = new CancellationTokenSource();
        SetProgress("Обновляю источники…");

        var testAfterRefresh = NodeStore.Settings.TestAfterRefresh;
        try
        {
            var progress = new Progress<string>(SetProgress);
            var summary = await NodeStore.RefreshAsync(progress, _work.Token);
            SetProgress(summary);
            AndroidSettingsStore.Current.LastSourceRefreshUtc = DateTime.UtcNow;
            AndroidSettingsStore.Save();
        }
        catch (System.OperationCanceledException)
        {
            SetProgress("Обновление отменено");
            testAfterRefresh = false;
        }
        catch (Exception exception)
        {
            AppLog.Write("sources", "Ошибка обновления источников", exception);
            SetProgress("Ошибка обновления: " + exception.Message);
            testAfterRefresh = false;
        }
        finally
        {
            _busy = false;
            UpdateSummary();
        }

        if (testAfterRefresh)
        {
            await RunTestAsync();
        }
    }

    private async Task RunTestAsync()
    {
        if (_busy)
        {
            Ui.Toast(this, "Дождитесь окончания операции");
            return;
        }

        if (ProbeClient.IsRunning)
        {
            Ui.Toast(this, "Проверка узлов уже идёт — дождитесь окончания");
            return;
        }

        _busy = true;
        _work = new CancellationTokenSource();
        ShowTestProgress(new ProbeProgress("Запускаю проверку узлов…", 0, 0, 0));

        try
        {
            var progress = new Progress<ProbeProgress>(ShowTestProgress);
            // Ручная проверка перепроверяет и рабочие узлы, иначе счётчики не менялись бы.
            var summary = await ProbeClient.RunBatchAsync(
                NodeTester.MaxBatch, _work.Token, progress, recheckWorking: true);
            SetProgress(MainActivity.DescribeTestResult(summary));
        }
        catch (Exception exception)
        {
            AppLog.Write("test", "Ошибка проверки узлов", exception);
            SetProgress("Ошибка проверки: " + exception.Message);
        }
        finally
        {
            _busy = false;
            HideTestProgress();
            UpdateSummary();
        }
    }

    /// <summary>Ход проверки: текст и полоска прогресса.</summary>
    private void ShowTestProgress(ProbeProgress progress) => RunOnUiThread(() =>
    {
        if (_progress is not null)
        {
            _progress.Text = progress.Describe();
        }

        if (_bar is not null)
        {
            _bar.Visibility = ViewStates.Visible;
            if (progress.Total > 0)
            {
                _bar.Indeterminate = false;
                _bar.Max = progress.Total;
                _bar.Progress = Math.Clamp(progress.Tested, 0, progress.Total);
            }
            else
            {
                _bar.Indeterminate = true;
            }
        }
    });

    private void HideTestProgress() => RunOnUiThread(() =>
    {
        if (_bar is not null)
        {
            _bar.Indeterminate = false;
            _bar.Visibility = ViewStates.Gone;
        }
    });

    private async Task MeasureAsync()
    {
        if (TunnelService.State != TunnelState.Running)
        {
            SetProgress("Сначала подключитесь к узлу: скорость меряется через туннель");
            return;
        }

        SetProgress("Замеряю скорость через узел…");
        var speed = await NetworkProbe.MeasureSpeedAsync(CancellationToken.None);
        SetProgress(speed is null ? "Замер не удался" : $"Скорость: {speed:0.#} Мбит/с");
        UpdateSummary();
    }

    private void ExportSelected()
    {
        var node = NodeStore.Selected ?? NodeStore.Snapshot().FirstOrDefault();
        if (node is null)
        {
            SetProgress("Нет узла для экспорта");
            return;
        }

        try
        {
            var send = new Intent(Intent.ActionSend)
                .SetType("text/plain")
                .PutExtra(Intent.ExtraText, ProfileExport.BuildShareLink(node))
                .PutExtra(Intent.ExtraSubject, "Узел VieNLink");
            StartActivity(Intent.CreateChooser(send, "Поделиться узлом"));
        }
        catch (Exception exception)
        {
            SetProgress("Не удалось поделиться: " + exception.Message);
        }
    }

    private void SetProgress(string text) => RunOnUiThread(() =>
    {
        if (_progress is not null)
        {
            _progress.Text = text;
        }
    });
}
