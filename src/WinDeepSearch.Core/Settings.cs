using System.Text.Json;

namespace WinDeepSearch.Core;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeepSearch");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string History => Path.Combine(Root, "history");
    public static string OcrCache => Path.Combine(Root, "ocr-cache");
}

public sealed class Settings
{
    public static readonly string[] DefaultIgnoredFolders =
        [".git", "node_modules", "bin", "obj", ".vs", ".idea", "__pycache__", "$Recycle.Bin", "System Volume Information"];

    /// <summary>Folder names skipped anywhere, or full paths skipped exactly.</summary>
    public List<string> IgnoredFolders { get; set; } = [.. DefaultIgnoredFolders];

    /// <summary>Lines shown above and below a match in the hover preview.</summary>
    public int ContextLines { get; set; } = 3;

    /// <summary>How many past searches to keep.</summary>
    public int HistoryLimit { get; set; } = 50;

    // The search bar toggles start the way they were last left.
    public bool UseRegex { get; set; }
    public bool MatchCase { get; set; }
    public bool ReadImages { get; set; } = true;

    public static Settings Load(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(file)) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Settings();
        }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, file, overwrite: true);
    }
}
