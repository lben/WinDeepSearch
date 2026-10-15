using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace WinDeepSearch.Core;

/// <summary>A file a worker is reading right now.</summary>
public sealed class ActiveFile(string path, long length, CancellationTokenSource skip)
{
    double _progress;

    public string Path { get; } = path;
    public long Length { get; } = length;
    public long StartedAt { get; } = Stopwatch.GetTimestamp();

    /// <summary>0 to 1.</summary>
    public double Progress
    {
        get => Volatile.Read(ref _progress);
        set => Volatile.Write(ref _progress, value);
    }

    /// <summary>What the worker is doing when it isn't plain reading, such as reading text in pictures.</summary>
    public volatile string? Detail;

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(StartedAt);

    /// <summary>Stops reading this file; the search moves on to the next one.</summary>
    public void Skip()
    {
        try
        {
            skip.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The file finished in the meantime.
        }
    }
}

/// <summary>
/// Searches folders, with everything under them, and single files on all cores. Results, skipped files and the
/// files being read are published in thread-safe collections for the window to poll.
/// </summary>
public sealed class SearchEngine
{
    readonly Matcher _matcher;
    readonly IReadOnlyList<string> _targets;
    readonly IReadOnlyList<string> _ignored;
    readonly ImageText? _images;
    readonly string _dataFolder;
    readonly Channel<(string Path, long Length, FileAttributes Attributes)> _files =
        Channel.CreateBounded<(string, long, FileAttributes)>(10_000);

    long _found, _done;
    volatile bool _enumerating = true;

    /// <param name="targets">Full paths of folders, searched with all their subfolders, and of single files.</param>
    /// <param name="dataFolder">
    /// The app's own data (history, remembered picture text), never searched: it holds earlier queries and
    /// results, which would otherwise top the results of every later search of a parent folder.
    /// </param>
    public SearchEngine(SearchQuery query, IReadOnlyList<string> targets, Settings settings, ImageText images, string dataFolder)
    {
        _matcher = new Matcher(query, settings.ContextLines);
        _targets = targets;
        _ignored = settings.IgnoredFolders;
        _images = query.ReadImages ? images : null;
        _dataFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder));
    }

    public ConcurrentQueue<FileResult> Results { get; } = new();
    public ConcurrentQueue<SkippedFile> Skipped { get; } = new();

    /// <summary>Files that couldn't be read, each with the reason in words for the window.</summary>
    public ConcurrentQueue<SkippedFile> Unreadable { get; } = new();
    public ConcurrentDictionary<string, ActiveFile> Active { get; } = new();
    public long FilesFound => Interlocked.Read(ref _found);
    public long FilesDone => Interlocked.Read(ref _done);
    public bool Enumerating => _enumerating;

    public async Task RunAsync(CancellationToken ct)
    {
        var producer = Task.Run(() => EnumerateAsync(ct), CancellationToken.None);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };
        try
        {
            await Parallel.ForEachAsync(_files.Reader.ReadAllAsync(ct), options, (file, token) =>
            {
                Process(file.Path, file.Length, file.Attributes, token);
                return ValueTask.CompletedTask;
            });
        }
        finally
        {
            await producer;
        }
    }

    async Task EnumerateAsync(CancellationToken ct)
    {
        try
        {
            var pending = new Stack<DirectoryInfo>();
            foreach (string target in Outermost(_targets))
            {
                // A file that is gone by now is still listed, and counted as unreadable.
                if (Directory.Exists(target)) pending.Push(new DirectoryInfo(target));
                else await Add(new FileInfo(target), ct);
            }

            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                IEnumerable<FileSystemInfo> entries;
                try
                {
                    entries = dir.EnumerateFileSystemInfos("*", options);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry is FileInfo file) await Add(file, ct);
                    else if (entry is DirectoryInfo sub && !IsIgnored(sub) && !IsLink(sub)
                             && !sub.FullName.Equals(_dataFolder, StringComparison.OrdinalIgnoreCase)) pending.Push(sub);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user.
        }
        finally
        {
            _enumerating = false;
            _files.Writer.Complete();
        }
    }

    /// <summary>
    /// Explorer's search results can select a folder together with something inside it; search that only once.
    /// </summary>
    static IEnumerable<string> Outermost(IReadOnlyList<string> targets) =>
        targets.Distinct(StringComparer.OrdinalIgnoreCase).Where(target => !targets.Any(other =>
            // A drive root ("D:\") already ends in a separator.
            target.StartsWith(Path.EndsInDirectorySeparator(other) ? other : other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !target.Equals(other, StringComparison.OrdinalIgnoreCase)));

    async Task Add(FileInfo file, CancellationToken ct)
    {
        Interlocked.Increment(ref _found);
        await _files.Writer.WriteAsync((file.FullName, file.Exists ? file.Length : 0, file.Exists ? file.Attributes : 0), ct);
    }

    bool IsIgnored(DirectoryInfo dir)
    {
        foreach (var rule in _ignored)
        {
            if (rule.Contains(Path.DirectorySeparatorChar) || rule.Contains(':'))
            {
                string full = Path.TrimEndingDirectorySeparator(rule);
                if (dir.FullName.Equals(full, StringComparison.OrdinalIgnoreCase) ||
                    dir.FullName.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (dir.Name.Equals(rule, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Junctions and symbolic links can loop back or repeat whole trees, so they are not followed.</summary>
    static bool IsLink(DirectoryInfo dir) =>
        (dir.Attributes & FileAttributes.ReparsePoint) != 0 && dir.LinkTarget != null;

    void Process(string path, long length, FileAttributes attributes, CancellationToken searchToken)
    {
        FileResult? result = null;
        using var skip = CancellationTokenSource.CreateLinkedTokenSource(searchToken);
        var active = new ActiveFile(path, length, skip);
        Active[path] = active;
        try
        {
            // Inside the try: a pattern can time out on a file name too.
            result = new FileResult { Path = path, NameMatches = _matcher.MatchName(System.IO.Path.GetFileName(path)) };
            // Reading a cloud file that isn't on this PC would download it; report it instead.
            if (Files.IsOnlineOnly(attributes)) Skipped.Enqueue(new SkippedFile(path, 0, "Online-only file, not downloaded"));
            else Content.Search(path, _matcher, _images, active, result, skip.Token);
        }
        catch (OperationCanceledException) when (!searchToken.IsCancellationRequested)
        {
            Skipped.Enqueue(new SkippedFile(path, active.Progress, "Skipped", length));
        }
        catch (OperationCanceledException)
        {
            // The whole search was stopped; keep what this file produced so far.
        }
        catch (RegexMatchTimeoutException)
        {
            Skipped.Enqueue(new SkippedFile(path, active.Progress, "The pattern took too long on this file"));
        }
        catch (Exception e)
        {
            // Locked, damaged or unsupported content must not stop the search.
            Unreadable.Enqueue(new SkippedFile(path, active.Progress, ReasonFor(e)));
        }
        finally
        {
            Active.TryRemove(path, out _);
            Interlocked.Increment(ref _done);
        }

        if (result != null && (result.NameMatches.Count > 0 || result.MatchCount > 0)) Results.Enqueue(result);
    }

    const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);

    /// <summary>Why a file couldn't be read, in words; the readers' own messages already say what went wrong.</summary>
    static string ReasonFor(Exception e) => e switch
    {
        UnauthorizedAccessException => "Access denied",
        FileNotFoundException or DirectoryNotFoundException => "Moved or deleted during the search",
        IOException { HResult: SharingViolation or LockViolation } => "In use by another program",
        // Office and OpenDocument files are zip archives; a password-protected one is not.
        InvalidDataException => "Damaged, or protected with a password",
        System.Xml.XmlException => "Damaged: its contents aren't valid",
        _ => e.Message,
    };
}
