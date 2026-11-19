using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MetadataDel.Core.CommandLine;

namespace MetadataDel.Cli;

// Explorer verbs run MetadataDel.exe with --log and no console, so problems would otherwise go unnoticed.
// A tray notification is non-blocking (unlike a message box) and disappears on its own.
internal static class ShellNotification
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);

    public static void Show(CliBatchSummary summary)
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive) return;
        var thread = new Thread(() => ShowCore(summary)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(Lifetime + TimeSpan.FromSeconds(2));
    }

    private static void ShowCore(CliBatchSummary summary)
    {
        try
        {
            Icon? appIcon = null;
            try { appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
            using var icon = new NotifyIcon { Icon = appIcon ?? SystemIcons.Warning, Text = "MetadataDel", Visible = true };
            icon.BalloonTipClicked += (_, _) => OpenLogs();
            var title = summary.Failed > 0 ? "Не все файлы обработаны" : "Метаданные удалены не полностью";
            var text = summary.Describe() + " Подробности и пояснения — в журнале MetadataDel. Нажмите, чтобы открыть папку.";
            icon.ShowBalloonTip((int)Lifetime.TotalMilliseconds, title, text, ToolTipIcon.Warning);
            var until = DateTime.UtcNow + Lifetime;
            while (DateTime.UtcNow < until)
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }
            icon.Visible = false;
            appIcon?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ExternalException) { }
    }

    private static void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.LogsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppDataPaths.LogsDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }
}
