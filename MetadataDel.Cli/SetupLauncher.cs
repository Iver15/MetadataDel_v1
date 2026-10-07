using System.Windows.Forms;

namespace MetadataDel.Cli;

internal static class SetupLauncher
{
    public static int Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new MainForm();
        Application.Run(form);
        return 0;
    }
}
