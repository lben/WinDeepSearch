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

    /// <summary>
    /// Opens the window for <paramref name="path"/>, or hands it to the copy that already did and returns false:
    /// this copy then has nothing left to do.
    /// </summary>
    public static bool Open(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Another copy is listening.
                if (HandOver(path)) return false;
                continue;
            }
            var window = new MainWindow(SearchTarget.FromPath(path));
            _ = CollectAsync(server, window);
            window.Show();
            return true;
        }
        // The pipe kept changing hands; this item gets a window of its own.
        new MainWindow(SearchTarget.FromPath(path)).Show();
        return true;
    }

    /// <summary>True once the first copy confirmed it took the item.</summary>
    static bool HandOver(string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            client.Write(Encoding.UTF8.GetBytes(path + "\n"));
            client.Flush();
            return client.ReadByte() == 1;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // The first copy stopped listening just now.
            return false;
        }
    }

    /// <summary>Runs on the window's thread: the awaits come back to it.</summary>
    static async Task CollectAsync(NamedPipeServerStream server, MainWindow window)
    {
        using (server)
        {
            var buffer = new byte[1];
            while (true)
            {
                using var quiet = new CancellationTokenSource(Quiet);
                try
                {
                    await server.WaitForConnectionAsync(quiet.Token);
                    using (var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true))
                    {
                        if (await reader.ReadLineAsync(quiet.Token) is { Length: > 0 } path) window.AddItem(path);
                    }
                    server.WriteByte(1);
                    server.Flush();
                    // The copy closes its end once it has read the confirmation.
                    while (await server.ReadAsync(buffer, quiet.Token) > 0) { }
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
