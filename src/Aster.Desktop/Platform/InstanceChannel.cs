// One Aster at a time for a launcher: "Aster --play <id>" from a TV home
// screen hands the title to the Aster already open (over a socket in
// $XDG_RUNTIME_DIR) and exits, instead of starting a second one.

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Aster.Desktop.Platform;

internal static class InstanceChannel
{
    private static string? SocketPath =>
        OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } dir
            ? Path.Combine(dir, "aster.sock")
            : null;

    /// <summary>
    /// True when an Aster already open took <paramref name="args"/>; false
    /// when none is listening (or a stale socket was left by a crash).
    /// </summary>
    public static bool TrySend(string[] args)
    {
        if (SocketPath is not { } path || !File.Exists(path)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            socket.Send(Encoding.UTF8.GetBytes(string.Join('\0', args) + "\n"));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Hears later launches' arguments, on a background thread; disposing stops and removes the socket.</summary>
    public static IDisposable? Listen(Action<string[]> received)
    {
        if (SocketPath is not { } path) return null;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(4);
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using var client = listener.Accept();
                        using var reader = new StreamReader(new NetworkStream(client), Encoding.UTF8);
                        if (reader.ReadLine() is { Length: > 0 } line) received(line.Split('\0'));
                    }
                    catch (SocketException) { return; }
                    catch (ObjectDisposedException) { return; }
                    catch (IOException) { }
                }
            }) { IsBackground = true, Name = "Aster instance channel" };
            thread.Start();
            return new Closer(listener, path);
        }
        catch (Exception error) when (error is SocketException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The value after <paramref name="name"/> in <paramref name="args"/>, if any.</summary>
    public static string? Value(string[] args, string name) =>
        Array.IndexOf(args, name) is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;

    private sealed class Closer(Socket listener, string path) : IDisposable
    {
        public void Dispose()
        {
            listener.Dispose();
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }
}
