namespace MetadataDel.Core.CommandLine;

/// <summary>Separates the desktop launcher from existing shell and CLI commands.</summary>
public static class DesktopEntryPoint
{
    /// <summary>Only a plain application launch opens a window.</summary>
    public static bool ShouldOpenMainWindow(string[] args) => args.Length == 0;
}
