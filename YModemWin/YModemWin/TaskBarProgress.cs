using System.Windows;
using System.Windows.Shell;

namespace YModemWin;

internal static class TaskBarProgress
{
    public static void SetValue(Window window, double progress)
    {
        window.Dispatcher.BeginInvoke(() =>
        {
            window.TaskbarItemInfo ??= new TaskbarItemInfo();
            var normalized = Math.Clamp(progress / 100.0, 0.0, 1.0);
            window.TaskbarItemInfo.ProgressState = normalized <= 0 ? TaskbarItemProgressState.None : TaskbarItemProgressState.Normal;
            window.TaskbarItemInfo.ProgressValue = normalized;
        });
    }
}
