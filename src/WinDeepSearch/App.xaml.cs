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

        switch (e.Args)
        {
            // The Windows 11 menu, for one item; the classic menu, for the empty space inside a folder.
            case ["--path", var path]:
                new SearchWindow(SearchTarget.FromPath(CleanPath(path))).Show();
                break;
            // The Windows 11 menu, for several items.
            case ["--items", var list]:
                new SearchWindow(SearchTarget.FromPaths(ReadList(list))).Show();
                break;
            // The classic menu, once per selected item.
            case ["--select", var path]:
                // A copy that handed its item over opens no window, so nothing else would end it.
                if (!Selection.Open(CleanPath(path))) Shutdown();
                break;
            default:
                new MainWindow().Show();
                break;
        }
    }

    /// <summary>
    /// Explorer passes a drive root quoted as "C:\", and the trailing backslash escapes the closing quote, so the
    /// path arrives as C:" — quotes can't be part of a Windows path, so drop them and put the root back.
    /// </summary>
    static string CleanPath(string path)
    {
        path = path.TrimEnd('"').Trim();
        return path.Length == 2 && path[1] == ':' ? path + @"\" : path;
    }

    /// <summary>Reads the list of selected items written by the Windows 11 menu command, then deletes it.</summary>
    static string[] ReadList(string file)
    {
        string[] paths = File.ReadAllLines(file).Where(line => line.Length > 0).ToArray();
        // The menu command names its lists like this; never delete any other file passed here.
        if (Path.GetFileName(file).StartsWith("WinDeepSearch-selection-", StringComparison.OrdinalIgnoreCase)) File.Delete(file);
        return paths;
    }

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
