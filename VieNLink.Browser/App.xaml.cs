using System.Windows;

namespace VieNLink;

/// <summary>Приложение «VieNLink Браузер»: отдельная установка, своя версия и обновления.</summary>
public partial class BrowserApp : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppLog.Initialize();
        AppPaths.EnsureDirectories();
        VpnClient.Start();
        base.OnStartup(e);
    }
}
