using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Экран серверов без списка: сводка (всего, рабочих, доступ по сервисам), обновление
/// и проверка в отдельном процессе. Подключение — с главного экрана и из шапки.
/// </summary>
#if DEBUG
[Activity(Label = "Серверы", Theme = "@style/Theme.VieNLink", Exported = true)]
#else
[Activity(Label = "Серверы", Theme = "@style/Theme.VieNLink")]
#endif
public sealed class NodesActivity : BaseActivity
{
    private TextView? _summary;
    private TextView? _detail;
    private TextView? _progress;
    private TextView? _testButton;
    private ProgressBar? _bar;

    private CancellationTokenSource? _work;
    private bool _busy;
    private bool _testing;
    private Handler? _handler;
    private Action? _tick;

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

        // Фоновая проверка идёт в отдельном процессе: без этого такта экран
        // показывал бы старые числа до следующего действия пользователя.
        _handler = new Handler(Looper.MainLooper!);
        _tick = () =>
        {
            if (!_busy)
            {
                UpdateSummary();
                if (ProbeClient.ActivityText is { } activity)
                {
                    SetProgress(activity);
                }
            }

            UpdateTestButtonText();
            _handler?.PostDelayed(_tick, 2000);
        };
        _handler.PostDelayed(_tick, 2000);
    }

    protected override void OnPause()
    {
        if (_handler is not null && _tick is not null)
        {
            _handler.RemoveCallbacks(_tick);
        }

        _handler = null;
        _tick = null;
        base.OnPause();
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
        var title = Ui.Title(this, "Серверы", P);
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
        _testButton = test;
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

        page.AddView(Ui.Section(this, "Как выбирается сервер", P));
        var how = Ui.Card(P, this,
            Bullet("Сначала личные подписки, затем маскированные (REALITY, Vision, WS/TLS), затем быстрые."),
            Bullet("Рабочий сервер — тот, через который реально открылся хотя бы один проверочный сайт."),
            Bullet("Лучший открывает больше заблокированных доменов; при равенстве — меньшая задержка."),
            Bullet($"Сервер с {NodeLimits.RemoveAfterFailures} провалами подряд удаляется; успешная проверка обнуляет счётчик."));
        page.AddView(how);

        var hint = Ui.Banner(P, this, P.Accent, "Почему списка нет",
            "В Windows таблица серверов скрыта так же: имена серверов не показываются. Приложение берёт " +
            "проверенный сервер по доступу к сервису и задержке, а вы видите только результат.");
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
            AppLog.Write("nodes", "Не удалось прочитать серверы", exception);
            SetProgress("Ошибка чтения серверов: " + exception.Message);
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
        // Один проход под блокировкой, без копии списка на 12 000 серверов:
        // экран обновляется каждые 2 секунды и не должен давать аллокации.
        var summary = NodeStore.DetailedSummary();

        if (_summary is not null)
        {
            _summary.Text = $"{summary.Total} серверов · доступных {summary.Available}";
        }

        if (_detail is not null)
        {
            var checkedText = summary.LastChecked is null
                ? "проверок ещё не было"
                : "проверено " + summary.LastChecked.Value.ToLocalTime().ToString("dd.MM HH:mm");
            _detail.Text = $"Доступ: универсальный {summary.Universal}, YouTube {summary.Youtube}, Telegram {summary.Telegram}, Discord {summary.Discord}\n" +
                           $"Всего рабочих: {summary.Working} · доступных: {summary.Available} · мои подписки: {summary.Mine} · " +
                           $"лимит {NodeStore.Settings.MaxTotalNodes} · " +
                           $"макс. доменов {summary.BestDomains} · {checkedText}";
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
        if (_testing)
        {
            // Повторное нажатие останавливает проверку — и ручную, и фоновую.
            _work?.Cancel();
            ProbeClient.Cancel();
            SetProgress("Поиск серверов остановлен");
            HideTestProgress();
            return;
        }

        if (_busy)
        {
            Ui.Toast(this, "Дождитесь окончания обновления");
            return;
        }

        if (ProbeClient.IsRunning)
        {
            // Идёт фоновая партия: останавливаем её, не запуская вторую.
            ProbeClient.Cancel();
            SetProgress("Поиск серверов остановлен");
            return;
        }

        // Явное действие пользователя снимает прежнюю остановку поиска.
        ProbeClient.Resume();
        _testing = true;
        _busy = true;
        _work = new CancellationTokenSource();
        ShowTestProgress(new ProbeProgress("Запускаю проверку серверов…", 0, 0, 0));

        try
        {
            // Несколько партий подряд по одному нажатию: 5 × 200 = до 1000 серверов.
            // Ручная проверка перепроверяет и рабочие серверы, иначе счётчики не менялись бы.
            var (summary, _) = await ProbeClient.RunBatchesAsync(
                ProbeClient.ManualBatches,
                NodeTester.MaxBatch,
                _work.Token,
                recheckWorking: true,
                batchStarted: (batch, total) =>
                    ShowTestProgress(new ProbeProgress($"Партия {batch} из {total}", 0, 0, 0)),
                progress: (batch, state) => ShowTestProgress(
                    state with { Stage = $"Партия {batch} из {ProbeClient.ManualBatches}" }));
            SetProgress(MainActivity.DescribeTestResult(summary));
        }
        catch (Exception exception)
        {
            AppLog.Write("test", "Ошибка проверки серверов", exception);
            SetProgress("Ошибка проверки: " + exception.Message);
        }
        finally
        {
            _testing = false;
            _busy = false;
            HideTestProgress();
            UpdateTestButtonText();
            UpdateSummary();
        }
    }

    /// <summary>Кнопка «Проверить» во время поиска становится «Остановить поиск».</summary>
    private void UpdateTestButtonText() => RunOnUiThread(() =>
    {
        if (_testButton is null)
        {
            return;
        }

        var searching = _testing || (ProbeClient.IsRunning && !ProbeClient.IsPaused);
        _testButton.Text = searching ? "Остановить поиск" : "Проверить";
    });

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
            SetProgress("Сначала подключитесь к серверу: скорость меряется через туннель");
            return;
        }

        SetProgress("Замеряю скорость через сервер…");
        var speed = await NetworkProbe.MeasureSpeedAsync(CancellationToken.None);
        SetProgress(speed is null ? "Замер не удался" : $"Скорость: {speed:0.#} Мбит/с");
        UpdateSummary();
    }

    private void ExportSelected()
    {
        var node = NodeStore.Selected ?? NodeStore.Snapshot().FirstOrDefault();
        if (node is null)
        {
            SetProgress("Нет сервера для экспорта");
            return;
        }

        try
        {
            var send = new Intent(Intent.ActionSend)
                .SetType("text/plain")
                .PutExtra(Intent.ExtraText, ProfileExport.BuildShareLink(node))
                .PutExtra(Intent.ExtraSubject, "Сервер VieNLink");
            StartActivity(Intent.CreateChooser(send, "Поделиться сервером"));
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
