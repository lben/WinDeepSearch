using System.IO;
using System.Windows;
using WinDeepSearch.Core;

namespace WinDeepSearch;

public partial class App : Application
{
    /// <summary>Settings shared by every window in this process.</summary>
    public static Settings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        switch (e.Args)
        {
            case ["--register"]:
                Shutdown(Registration.Register());
                return;
            case ["--unregister"]:
                Shutdown(Registration.Unregister());
                return;
        }

        Settings = Settings.Load(AppPaths.SettingsFile);
        ImageDecoder.Fallback = WicDecoder.Decode;

        string? path = e.Args is ["--path", var p] ? CleanPath(p) : null;
        Window window = path != null ? new SearchWindow(SearchTarget.FromPath(path)) : new MainWindow();
        window.Show();
    }

    /// <summary>
    /// Explorer passes "C:\" quoted as "C:\", and the trailing backslash escapes the closing quote, so the path
    /// arrives as C:" — quotes can't be part of a Windows path, so drop them.
    /// </summary>
    static string CleanPath(string path) => path.TrimEnd('"').Trim();

    public static void SaveSettings()
    {
        try
        {
            Settings.Save(AppPaths.SettingsFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another window is writing the same file; this change is kept for this session.
        }
    }
}
