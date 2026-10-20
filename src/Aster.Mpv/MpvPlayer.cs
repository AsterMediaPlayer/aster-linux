using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Aster.Mpv;

/// <summary>Why a file stopped playing, and mpv's message when it was an error.</summary>
public sealed record MpvEndFile(MpvEndFileReason Reason, string? ErrorMessage);

/// <summary>
/// One mpv instance. Its events are read on a thread of their own and
/// raised there, so handlers that touch the UI must hop to it. Commands
/// sent with <see cref="CommandAsync"/> complete when mpv replies.
/// </summary>
public sealed unsafe class MpvPlayer : IDisposable
{
    private IntPtr handle;
    private readonly Thread eventThread;
    private volatile bool disposing;
    private long nextReply;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource> pendingCommands = new();
    private readonly ConcurrentDictionary<ulong, string> observed = new();
    private TaskCompletionSource? renderContextReleased;

    /// <summary>A watched property changed: its name and new value (double, bool, string, or null).</summary>
    public event Action<string, object?>? PropertyChanged;

    /// <summary>A file opened and its tracks are known.</summary>
    public event Action? FileLoaded;

    /// <summary>A file stopped playing: at its end, on a new load, or on an error.</summary>
    public event Action<MpvEndFile>? EndFile;

    /// <summary>Playback is running again after a load or seek.</summary>
    public event Action? PlaybackRestart;

    /// <summary>mpv's own messages, from the level asked for in <see cref="Create"/>.</summary>
    public event Action<string, string, string>? Log;

    private MpvPlayer(IntPtr handle)
    {
        this.handle = handle;
        eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
    }

    /// <summary>Makes and starts an mpv with <paramref name="options"/> set before it starts.</summary>
    public static MpvPlayer Create(IEnumerable<KeyValuePair<string, string>> options, string logLevel = "warn")
    {
        var handle = MpvNative.mpv_create();
        if (handle == IntPtr.Zero) throw new MpvException("mpv couldn't be created.");
        try
        {
            foreach (var (name, value) in options)
            {
                MpvException.Check(MpvNative.mpv_set_option_string(handle, name, value), $"Setting {name}");
            }
            MpvException.Check(MpvNative.mpv_initialize(handle), "Starting mpv");
            MpvNative.mpv_request_log_messages(handle, logLevel);
        }
        catch
        {
            MpvNative.mpv_terminate_destroy(handle);
            throw;
        }
        var player = new MpvPlayer(handle);
        player.eventThread.Start();
        return player;
    }

    internal IntPtr Handle => handle != IntPtr.Zero ? handle : throw new ObjectDisposedException(nameof(MpvPlayer));

    // ---- Properties ----

    public void SetProperty(string name, string value) =>
        MpvException.Check(MpvNative.mpv_set_property_string(Handle, name, value), $"Setting {name}");

    public void SetProperty(string name, bool value) => SetProperty(name, value ? "yes" : "no");

    public void SetProperty(string name, double value) => SetProperty(name, value.ToString(CultureInfo.InvariantCulture));

    public string? GetString(string name)
    {
        var value = MpvNative.mpv_get_property_string(Handle, name);
        if (value == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(value); }
        finally { MpvNative.mpv_free(value); }
    }

    /// <summary>A number property, or null while it has no value (no file, say).</summary>
    public double? GetDouble(string name)
    {
        double value;
        return MpvNative.mpv_get_property(Handle, name, MpvFormat.Double, &value) >= 0 ? value : null;
    }

    public bool? GetFlag(string name)
    {
        int value;
        return MpvNative.mpv_get_property(Handle, name, MpvFormat.Flag, &value) >= 0 ? value != 0 : null;
    }

    /// <summary>
    /// The file's tracks. mpv gives node properties as JSON when asked for
    /// a string, which saves walking its node structures by hand.
    /// </summary>
    public IReadOnlyList<MpvTrack> Tracks() =>
        GetString("track-list") is { } json ? JsonSerializer.Deserialize<List<MpvTrack>>(json) ?? [] : [];

    /// <summary>
    /// Raises <see cref="PropertyChanged"/> for <paramref name="name"/> from now
    /// on, as a double, a bool, a string, or with no value (just "it changed").
    /// </summary>
    public void Observe(string name, Type? valueType)
    {
        var format = valueType == typeof(double) ? MpvFormat.Double
            : valueType == typeof(bool) ? MpvFormat.Flag
            : valueType == typeof(string) ? MpvFormat.String
            : MpvFormat.None;
        var id = (ulong)Interlocked.Increment(ref nextReply);
        observed[id] = name;
        MpvException.Check(MpvNative.mpv_observe_property(Handle, id, name, format), $"Watching {name}");
    }

    // ---- Commands ----

    /// <summary>Runs a command and waits for it, on the calling thread. For quick ones.</summary>
    public void Command(params string[] args) => WithArgs(args, argv => MpvException.Check(MpvNative.mpv_command(Handle, argv), args[0]));

    /// <summary>Sends a command; the task ends when mpv has run it.</summary>
    public Task CommandAsync(params string[] args)
    {
        var id = (ulong)Interlocked.Increment(ref nextReply);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingCommands[id] = reply;
        try
        {
            WithArgs(args, argv => MpvException.Check(MpvNative.mpv_command_async(Handle, id, argv), args[0]));
        }
        catch
        {
            pendingCommands.TryRemove(id, out _);
            throw;
        }
        return reply.Task;
    }

    private delegate void ArgvCall(IntPtr* argv);

    private static void WithArgs(string[] args, ArgvCall call)
    {
        var argv = stackalloc IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) argv[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            argv[args.Length] = IntPtr.Zero;
            call(argv);
        }
        finally
        {
            for (var i = 0; i < args.Length; i++) Marshal.FreeCoTaskMem(argv[i]);
        }
    }

    // ---- Events ----

    private void EventLoop()
    {
        while (!disposing)
        {
            var ev = MpvNative.mpv_wait_event(handle, -1);
            if (disposing || ev->EventId == MpvEventId.Shutdown) break;
            try { Dispatch(ev); }
            catch (Exception error)
            {
                // A handler's failure mustn't end the loop, or every later
                // event would be lost.
                Log?.Invoke("aster", "error", error.ToString());
            }
        }
        foreach (var (_, reply) in pendingCommands) reply.TrySetCanceled();
        pendingCommands.Clear();
    }

    private void Dispatch(MpvEvent* ev)
    {
        switch (ev->EventId)
        {
            case MpvEventId.CommandReply:
                if (pendingCommands.TryRemove(ev->ReplyUserdata, out var reply))
                {
                    if (ev->Error < 0) reply.TrySetException(new MpvException(MpvNative.ErrorString(ev->Error), ev->Error));
                    else reply.TrySetResult();
                }
                break;
            case MpvEventId.PropertyChange:
                var property = (MpvEventProperty*)ev->Data;
                if (observed.TryGetValue(ev->ReplyUserdata, out var name)) PropertyChanged?.Invoke(name, Value(property));
                break;
            case MpvEventId.FileLoaded:
                FileLoaded?.Invoke();
                break;
            case MpvEventId.EndFile:
                var end = (MpvEventEndFile*)ev->Data;
                EndFile?.Invoke(new MpvEndFile(end->Reason, end->Reason == MpvEndFileReason.Error ? MpvNative.ErrorString(end->Error) : null));
                break;
            case MpvEventId.PlaybackRestart:
                PlaybackRestart?.Invoke();
                break;
            case MpvEventId.LogMessage:
                var message = (MpvEventLogMessage*)ev->Data;
                Log?.Invoke(
                    Marshal.PtrToStringUTF8(message->Prefix) ?? "",
                    Marshal.PtrToStringUTF8(message->Level) ?? "",
                    (Marshal.PtrToStringUTF8(message->Text) ?? "").TrimEnd());
                break;
        }
    }

    private static object? Value(MpvEventProperty* property)
    {
        if (property->Data == IntPtr.Zero) return null;
        return property->Format switch
        {
            MpvFormat.Double => *(double*)property->Data,
            MpvFormat.Flag => *(int*)property->Data != 0,
            MpvFormat.Int64 => *(long*)property->Data,
            MpvFormat.String or MpvFormat.OsdString => Marshal.PtrToStringUTF8(*(IntPtr*)property->Data),
            _ => null,
        };
    }

    // ---- Rendering and shutdown ----

    /// <summary>A render context is drawing this player's video; mpv must outlive it.</summary>
    internal void RenderContextCreated() =>
        renderContextReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void RenderContextFreed() => renderContextReleased?.TrySetResult();

    /// <summary>
    /// Ends mpv in the background. mpv requires its render context to be
    /// freed first, which the video view does when it leaves the screen, so
    /// this waits for that (for a while).
    /// </summary>
    public void Dispose()
    {
        if (disposing) return;
        disposing = true;
        var waitForRender = renderContextReleased?.Task ?? Task.CompletedTask;
        var stopping = handle;
        Task.WhenAny(waitForRender, Task.Delay(TimeSpan.FromSeconds(5))).ContinueWith(_ =>
        {
            MpvNative.mpv_wakeup(stopping);
            eventThread.Join();
            handle = IntPtr.Zero;
            MpvNative.mpv_terminate_destroy(stopping);
        }, TaskScheduler.Default);
    }
}
