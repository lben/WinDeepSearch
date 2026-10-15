using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace WinDeepSearch;

/// <summary>
/// The classic right-click menu starts one copy of the app per selected item. The first copy opens the search
/// window and listens on a pipe; the copies started with it hand their item over and exit, and the window adds
/// it. The pipe closes once no item has arrived for a moment.
/// </summary>
static class Selection
{
    static readonly string PipeName = $"WinDeepSearch.Selection.{Process.GetCurrentProcess().SessionId}";
    static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1.5);

    /// <summary>Opens the window for <paramref name="path"/>, or hands it to the copy that already did.</summary>
    public static void Open(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException)
            {
                // Another copy is listening.
                if (HandOver(path)) return;
                continue;
            }
            var window = new SearchWindow(SearchTarget.FromPath(path));
            window.Show();
            _ = CollectAsync(server, window);
            return;
        }
        // The pipe kept changing hands; this item gets a window of its own.
        new SearchWindow(SearchTarget.FromPath(path)).Show();
    }

    static bool HandOver(string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            client.Write(Encoding.UTF8.GetBytes(path));
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException)
        {
            // The first copy stopped listening just now.
            return false;
        }
    }

    /// <summary>Runs on the window's thread: the awaits come back to it.</summary>
    static async Task CollectAsync(NamedPipeServerStream server, SearchWindow window)
    {
        using (server)
        {
            while (true)
            {
                using var quiet = new CancellationTokenSource(Quiet);
                try
                {
                    await server.WaitForConnectionAsync(quiet.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                    string path = await reader.ReadToEndAsync(quiet.Token);
                    if (path.Length > 0) window.AddItem(path);
                    server.Disconnect();
                }
                catch (Exception e) when (e is OperationCanceledException or IOException)
                {
                    // No more items, or a copy broke off midway. Copies started later open their own window.
                    return;
                }
            }
        }
    }
}
