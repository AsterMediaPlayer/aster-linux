using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aster.Core;

namespace Aster.Desktop.Platform;

/// <summary>
/// <c>aster://</c> links opened while Aster is already running: the
/// launch the link starts hands it to the running one, over a socket of
/// the user's own, and leaves, rather than opening a second Aster. The
/// running one listens from when it starts.
/// </summary>
public static class DeepLinks
{
    /// <summary>In the session's runtime folder, which only the user can reach; else Aster's own data folder.</summary>
    private static string SocketPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? runtime : Aster.Core.Jellyfin.AppPaths.Data,
        // Its own: the TV home screen's --play hand-off (InstanceChannel) has aster.sock.
        "aster-links.sock");

    /// <summary>The link to the Aster that's running, if one is; false when none is (this launch is the app).</summary>
    public static bool HandOff(DeepLink link)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send(Encoding.UTF8.GetBytes(link + "\n"));
            return true;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>Hears links from later launches for as long as the app runs; each is handed to <paramref name="follow"/>.</summary>
    public static void Listen(Action<DeepLink> follow)
    {
        Socket listener;
        try
        {
            // Left by an Aster that didn't close properly (no one answered HandOff): taken over.
            File.Delete(SocketPath);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            listener.Listen(4);
        }
        catch (Exception error) when (error is SocketException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"Links: can't listen for aster:// links from other launches ({error.Message})");
            return;
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var client = await listener.AcceptAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(new NetworkStream(client), Encoding.UTF8);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                    if (DeepLink.Parse(line) is { } link) follow(link);
                }
                catch (Exception error) when (error is SocketException or IOException or OperationCanceledException) { }
                catch (ObjectDisposedException) { return; }
            }
        });
    }
}
