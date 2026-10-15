using System.Text.Json;

namespace WinDeepSearch.Core;

/// <param name="Root">The folder searched; for a selection, the folder the selected items are in.</param>
/// <param name="SingleFile">Set when only this file was searched.</param>
/// <param name="Items">Set when the search covered these selected files and folders only.</param>
public sealed record HistorySummary(
    string Id,
    DateTimeOffset StartedAt,
    double Seconds,
    SearchQuery Query,
    string Root,
    string? SingleFile,
    int NameMatches,
    int Matches,
    int FilesWithMatches,
    int Skipped,
    bool Stopped,
    IReadOnlyList<string>? Items = null);

/// <summary>Past searches with their results, one pair of files per search so several windows can write safely.</summary>
public sealed class HistoryStore(string dir)
{
    const string SummarySuffix = ".summary.json";
    const string ResultsSuffix = ".results.json";

    public void Save(HistorySummary summary, IReadOnlyList<FileResult> results, int keep)
    {
        Directory.CreateDirectory(dir);
        Write(Path.Combine(dir, summary.Id + ResultsSuffix), results);
        // The summary is written last: an entry is listed only once its results are complete.
        Write(Path.Combine(dir, summary.Id + SummarySuffix), summary);

        foreach (var old in List().Skip(Math.Max(0, keep))) Delete(old.Id);
    }

    public static string NewId() => DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Newest first.</summary>
    public IReadOnlyList<HistorySummary> List()
    {
        if (!Directory.Exists(dir)) return [];
        var list = new List<HistorySummary>();
        foreach (var file in Directory.EnumerateFiles(dir, "*" + SummarySuffix))
        {
            try
            {
                if (JsonSerializer.Deserialize<HistorySummary>(File.ReadAllBytes(file)) is { } summary) list.Add(summary);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Skip an entry another window is deleting, or one that was damaged.
            }
        }
        return list.OrderByDescending(s => s.StartedAt).ThenByDescending(s => s.Id, StringComparer.Ordinal).ToList();
    }

    public List<FileResult> LoadResults(string id)
    {
        try
        {
            return JsonSerializer.Deserialize<List<FileResult>>(File.ReadAllBytes(Path.Combine(dir, id + ResultsSuffix))) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Delete(string id)
    {
        File.Delete(Path.Combine(dir, id + SummarySuffix));
        File.Delete(Path.Combine(dir, id + ResultsSuffix));
    }

    public void Clear()
    {
        foreach (var summary in List()) Delete(summary.Id);
    }

    static void Write<T>(string file, T value)
    {
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = File.Create(temp)) JsonSerializer.Serialize(stream, value);
        File.Move(temp, file, overwrite: true);
    }
}
