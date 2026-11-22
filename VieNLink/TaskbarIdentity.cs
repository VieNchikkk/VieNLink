using System.Runtime.InteropServices;

namespace VieNLink;

/// <summary>
/// Единый идентификатор приложения для панели задач Windows. Без него закреплённый
/// ярлык и ярлык на рабочем столе иногда считаются разными приложениями, и при запуске
/// появляется вторая кнопка вместо уже закреплённой.
/// </summary>
internal static class TaskbarIdentity
{
    /// <summary>Один и тот же ID прописан и в ярлыках установщика (build/installer.iss).</summary>
    public const string AppUserModelId = "VieN.VieNLink";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    public static void Apply()
    {
        try
        {
            var result = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            if (result != 0)
            {
                AppLog.Write("app", $"AppUserModelID не задан: HRESULT 0x{result:X8}");
            }
            else
            {
                AppLog.Write("app", "AppUserModelID: " + AppUserModelId);
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("app", "Не удалось задать AppUserModelID", exception);
        }
    }
}
