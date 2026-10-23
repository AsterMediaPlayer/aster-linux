// Linux's media controls: an MPRIS player on the session bus, which KDE's
// and GNOME's media applets, the lock screen, playerctl and (through them)
// headsets drive. The bus is reached through Tmds.DBus.Protocol, which
// Avalonia brings for its own Linux features. Calls come on the bus's
// thread; AudioSession brings them to the UI thread.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Aster.Desktop.Audio;

[SupportedOSPlatform("linux")]
internal sealed class MprisMediaControls : ISystemMediaControls, IPathMethodHandler
{
    private const string BusName = "org.mpris.MediaPlayer2.aster";
    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private readonly DBusConnection connection;
    private readonly object gate = new();
    private readonly string artFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aster-{Environment.UserName}-art");

    // What's shown, as of when it was last told, for the bus's thread to read.
    private NowPlayingInfo? info;
    private string? artUrl;
    private DateTimeOffset at;

    private MprisMediaControls(DBusConnection connection)
    {
        this.connection = connection;
    }

    /// <summary>Connects to the session bus and takes the player's name, in the background; calls before then are kept until it's up.</summary>
    public static MprisMediaControls Start()
    {
        var address = DBusAddress.Session ?? throw new InvalidOperationException("No D-Bus session bus");
        var controls = new MprisMediaControls(new DBusConnection(address));
        _ = controls.ConnectAsync();
        return controls;
    }

    private async Task ConnectAsync()
    {
        try
        {
            await connection.ConnectAsync();
            connection.AddMethodHandler(this);
            // A second copy of the app gets a name of its own, as MPRIS allows.
            try { await connection.RequestNameAsync(BusName, RequestNameOptions.None); }
            catch (DBusExceptionBase) { await connection.RequestNameAsync($"{BusName}.instance{Environment.ProcessId}", RequestNameOptions.None); }
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Media controls: MPRIS unavailable ({error.Message})");
        }
    }

    public event Action<MediaCommand>? Command;
    public event Action<double>? SeekRequested;

    public string Path => ObjectPath;
    public bool HandlesChildPaths => false;

    public void Show(NowPlayingInfo? next, byte[]? artwork)
    {
        NowPlayingInfo? before;
        lock (gate)
        {
            before = info;
            if (artwork is not null && next is not null) artUrl = SaveArtwork(next.Id, artwork);
            else if (next is null || next.Id != before?.Id) artUrl = null;
            // Where the last one would have got to by now, to tell a seek from playing on.
            var expected = before is { IsPlaying: true } ? before.Position + (DateTimeOffset.UtcNow - at).TotalSeconds * before.Rate : before?.Position ?? 0;
            info = next;
            at = DateTimeOffset.UtcNow;
            if (next is not null && before?.Id == next.Id && Math.Abs(next.Position - expected) > 2) SendSeeked(next.Position);
        }
        SendPropertiesChanged();
    }

    /// <summary>The cover as a file, which every MPRIS client can show (an address on the server might need signing in).</summary>
    private string? SaveArtwork(string id, byte[] artwork)
    {
        try
        {
            Directory.CreateDirectory(artFolder);
            foreach (var old in Directory.EnumerateFiles(artFolder)) File.Delete(old);
            var file = System.IO.Path.Combine(artFolder, $"{Sanitize(id)}.jpg");
            File.WriteAllBytes(file, artwork);
            return new Uri(file).AbsoluteUri;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ---- Calls from the bus ----

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        var member = request.MemberAsString;
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([Introspection], Array.Empty<string>());
            return ValueTask.CompletedTask;
        }
        switch (request.InterfaceAsString, member)
        {
            case (PlayerInterface, "Play"): Reply(context, MediaCommand.Play); break;
            case (PlayerInterface, "Pause"): Reply(context, MediaCommand.Pause); break;
            case (PlayerInterface, "PlayPause"): Reply(context, MediaCommand.TogglePlayPause); break;
            case (PlayerInterface, "Stop"): Reply(context, MediaCommand.Stop); break;
            case (PlayerInterface, "Next"): Reply(context, MediaCommand.Next); break;
            case (PlayerInterface, "Previous"): Reply(context, MediaCommand.Previous); break;
            case (PlayerInterface, "Seek"):
            {
                var offset = request.GetBodyReader().ReadInt64() / 1_000_000.0;
                ReplyEmpty(context);
                SeekRequested?.Invoke(Math.Max(0, CurrentPosition() + offset));
                break;
            }
            case (PlayerInterface, "SetPosition"):
            {
                var reader = request.GetBodyReader();
                var track = reader.ReadObjectPathAsString();
                var position = reader.ReadInt64() / 1_000_000.0;
                ReplyEmpty(context);
                // Only for what's playing now, as MPRIS says.
                if (track == TrackId(Snapshot())) SeekRequested?.Invoke(Math.Max(0, position));
                break;
            }
            case (PlayerInterface, "OpenUri"):
                context.ReplyError("org.freedesktop.DBus.Error.NotSupported", "Aster plays from its own library");
                break;
            case (RootInterface, "Raise" or "Quit"):
                ReplyEmpty(context);
                break;
            case (PropertiesInterface, "Get"):
            {
                var reader = request.GetBodyReader();
                var iface = reader.ReadString();
                var name = reader.ReadString();
                if (!Properties(iface).Contains(name))
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {name}");
                    break;
                }
                var writer = context.CreateReplyWriter("v");
                try
                {
                    WriteProperty(ref writer, name, Snapshot());
                    context.Reply(writer.CreateMessage());
                }
                finally { writer.Dispose(); }
                break;
            }
            case (PropertiesInterface, "GetAll"):
            {
                var iface = request.GetBodyReader().ReadString();
                var writer = context.CreateReplyWriter("a{sv}");
                try
                {
                    WriteProperties(ref writer, Properties(iface), Snapshot());
                    context.Reply(writer.CreateMessage());
                }
                finally { writer.Dispose(); }
                break;
            }
            case (PropertiesInterface, "Set"):
                // Rate and Volume are left to the app's own controls.
                ReplyEmpty(context);
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }
        return ValueTask.CompletedTask;
    }

    private void Reply(MethodContext context, MediaCommand command)
    {
        ReplyEmpty(context);
        Command?.Invoke(command);
    }

    private static void ReplyEmpty(MethodContext context)
    {
        if (context.NoReplyExpected) return;
        using var writer = context.CreateReplyWriter(null!);
        context.Reply(writer.CreateMessage());
    }

    private (NowPlayingInfo? Info, string? ArtUrl, double Position) Snapshot()
    {
        lock (gate) return (info, artUrl, CurrentPositionLocked());
    }

    private double CurrentPosition()
    {
        lock (gate) return CurrentPositionLocked();
    }

    private double CurrentPositionLocked() =>
        info is not { } now ? 0
        : now.IsPlaying ? Math.Min(now.Position + (DateTimeOffset.UtcNow - at).TotalSeconds * now.Rate, now.Duration)
        : now.Position;

    // ---- Properties ----

    private static readonly string[] RootProperties = ["CanQuit", "CanRaise", "HasTrackList", "Identity", "SupportedUriSchemes", "SupportedMimeTypes"];

    private static readonly string[] PlayerProperties =
    [
        "PlaybackStatus", "Rate", "MinimumRate", "MaximumRate", "Metadata", "Volume", "Position",
        "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause", "CanSeek", "CanControl",
    ];

    /// <summary>What changes as playback does; Position is read when wanted, as MPRIS says.</summary>
    private static readonly string[] ChangingProperties = ["PlaybackStatus", "Rate", "Metadata", "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause", "CanSeek"];

    private static string[] Properties(string iface) => iface switch
    {
        RootInterface => RootProperties,
        PlayerInterface => PlayerProperties,
        _ => [],
    };

    private static void WriteProperties(ref MessageWriter writer, IEnumerable<string> names, (NowPlayingInfo? Info, string? ArtUrl, double Position) state)
    {
        var dictionary = writer.WriteDictionaryStart();
        foreach (var name in names)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(name);
            WriteProperty(ref writer, name, state);
        }
        writer.WriteDictionaryEnd(dictionary);
    }

    private static void WriteProperty(ref MessageWriter writer, string name, (NowPlayingInfo? Info, string? ArtUrl, double Position) state)
    {
        var now = state.Info;
        switch (name)
        {
            case "CanQuit" or "CanRaise" or "HasTrackList": writer.WriteVariantBool(false); break;
            case "Identity": writer.WriteVariantString("Aster"); break;
            case "SupportedUriSchemes" or "SupportedMimeTypes": WriteStrings(ref writer, []); break;
            case "PlaybackStatus": writer.WriteVariantString(now is null ? "Stopped" : now.IsPlaying ? "Playing" : "Paused"); break;
            case "Rate": writer.WriteVariantDouble(now?.Rate ?? 1); break;
            case "MinimumRate": writer.WriteVariantDouble(now is { IsBook: true } ? 0.5 : 1); break;
            case "MaximumRate": writer.WriteVariantDouble(now is { IsBook: true } ? 3 : 1); break;
            case "Volume": writer.WriteVariantDouble(1); break;
            case "Position": writer.WriteVariantInt64((long)(state.Position * 1_000_000)); break;
            case "CanGoNext": writer.WriteVariantBool(now?.CanGoNext ?? false); break;
            case "CanGoPrevious": writer.WriteVariantBool(now?.CanGoPrevious ?? false); break;
            case "CanPlay" or "CanPause" or "CanSeek": writer.WriteVariantBool(now is not null); break;
            case "CanControl": writer.WriteVariantBool(true); break;
            case "Metadata": WriteMetadata(ref writer, now, state.ArtUrl); break;
        }
    }

    private static void WriteMetadata(ref MessageWriter writer, NowPlayingInfo? now, string? artUrl)
    {
        writer.WriteSignature("a{sv}");
        var dictionary = writer.WriteDictionaryStart();
        if (now is not null)
        {
            Entry(ref writer, "mpris:trackid");
            writer.WriteVariantObjectPath(TrackId(now));
            Entry(ref writer, "mpris:length");
            writer.WriteVariantInt64((long)(Math.Max(now.Duration, 0) * 1_000_000));
            Entry(ref writer, "xesam:title");
            writer.WriteVariantString(now.Title);
            if (now.Artist is { } artist)
            {
                Entry(ref writer, "xesam:artist");
                WriteStrings(ref writer, [artist]);
            }
            if (now.Album is { } album)
            {
                Entry(ref writer, "xesam:album");
                writer.WriteVariantString(album);
            }
            if (artUrl is not null)
            {
                Entry(ref writer, "mpris:artUrl");
                writer.WriteVariantString(artUrl);
            }
        }
        writer.WriteDictionaryEnd(dictionary);
    }

    private static void Entry(ref MessageWriter writer, string key)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
    }

    /// <summary>A variant holding an array of strings.</summary>
    private static void WriteStrings(ref MessageWriter writer, IReadOnlyList<string> values)
    {
        writer.WriteSignature("as");
        var array = writer.WriteArrayStart(DBusType.String);
        foreach (var value in values) writer.WriteString(value);
        writer.WriteArrayEnd(array);
    }

    private static string TrackId((NowPlayingInfo? Info, string? ArtUrl, double Position) state) =>
        state.Info is { } now ? TrackId(now) : "/org/mpris/MediaPlayer2/TrackList/NoTrack";

    private static string TrackId(NowPlayingInfo now) => $"/org/astermediaplayer/track/{Sanitize(now.Id)}";

    /// <summary>Letters, digits and underscores only, as an object path's elements must be.</summary>
    private static string Sanitize(string id) => new(id.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

    // ---- Signals ----

    private void SendPropertiesChanged()
    {
        try
        {
            var state = Snapshot();
            var writer = connection.GetMessageWriter();
            try
            {
                writer.WriteSignalHeader(null, ObjectPath, PropertiesInterface, "PropertiesChanged", "sa{sv}as");
                writer.WriteString(PlayerInterface);
                WriteProperties(ref writer, ChangingProperties, state);
                var invalidated = writer.WriteArrayStart(DBusType.String);
                writer.WriteArrayEnd(invalidated);
                connection.TrySendMessage(writer.CreateMessage());
            }
            finally { writer.Dispose(); }
        }
        catch (Exception error) when (error is DBusExceptionBase or ObjectDisposedException or InvalidOperationException)
        {
            // Not connected yet, or the bus went away: the next change tries again.
        }
    }

    private void SendSeeked(double position)
    {
        try
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteSignalHeader(null, ObjectPath, PlayerInterface, "Seeked", "x");
            writer.WriteInt64((long)(position * 1_000_000));
            connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception error) when (error is DBusExceptionBase or ObjectDisposedException or InvalidOperationException) { }
    }

    private static readonly ReadOnlyMemory<byte> Introspection = Encoding.UTF8.GetBytes("""
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/>
          <method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/>
          <property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/>
          <property name="Identity" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/>
          <method name="Previous"/>
          <method name="Pause"/>
          <method name="PlayPause"/>
          <method name="Stop"/>
          <method name="Play"/>
          <method name="Seek"><arg direction="in" name="Offset" type="x"/></method>
          <method name="SetPosition"><arg direction="in" name="TrackId" type="o"/><arg direction="in" name="Position" type="x"/></method>
          <method name="OpenUri"><arg direction="in" name="Uri" type="s"/></method>
          <signal name="Seeked"><arg name="Position" type="x"/></signal>
          <property name="PlaybackStatus" type="s" access="read"/>
          <property name="Rate" type="d" access="readwrite"/>
          <property name="Metadata" type="a{sv}" access="read"/>
          <property name="Volume" type="d" access="readwrite"/>
          <property name="Position" type="x" access="read"/>
          <property name="MinimumRate" type="d" access="read"/>
          <property name="MaximumRate" type="d" access="read"/>
          <property name="CanGoNext" type="b" access="read"/>
          <property name="CanGoPrevious" type="b" access="read"/>
          <property name="CanPlay" type="b" access="read"/>
          <property name="CanPause" type="b" access="read"/>
          <property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"/>
        </interface>
        """);

    public void Dispose()
    {
        connection.Dispose();
        try { if (Directory.Exists(artFolder)) Directory.Delete(artFolder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
