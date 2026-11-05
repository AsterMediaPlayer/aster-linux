// The parts of libmpv's C API (client.h, render.h, render_gl.h) the app
// uses. Names and values are mpv's own; see those headers for the details.

using System.Runtime.InteropServices;

namespace Aster.Mpv;

internal enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

public enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5,
}

internal enum MpvRenderParamType
{
    Invalid = 0,
    ApiType = 1,
    OpenGlInitParams = 2,
    OpenGlFbo = 3,
    FlipY = 4,
    X11Display = 8,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public IntPtr Prefix;
    public IntPtr Level;
    public IntPtr Text;
    public int LogLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvRenderParam
{
    public MpvRenderParamType Type;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlInitParams
{
    public IntPtr GetProcAddress;
    public IntPtr GetProcAddressContext;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlFbo
{
    public int Fbo;
    public int Width;
    public int Height;
    public int InternalFormat;
}

internal static unsafe partial class MpvNative
{
    private const string Library = "libmpv-2";

    /// <summary>
    /// libmpv goes by another name on each system: libmpv-2.dll beside the
    /// app on Windows, the distribution's libmpv.so.2 on Linux (from apt's
    /// libmpv2), libmpv.2.dylib on a Mac.
    /// </summary>
    static MpvNative()
    {
        string[] names = OperatingSystem.IsWindows() ? ["libmpv-2.dll", "mpv-2.dll"]
            : OperatingSystem.IsMacOS() ? ["libmpv.2.dylib", "libmpv.dylib"]
            : ["libmpv.so.2", "libmpv.so"];
        NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, (name, assembly, path) =>
        {
            if (name != Library) return IntPtr.Zero;
            foreach (var candidate in names)
            {
                if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle)) return handle;
            }
            return IntPtr.Zero;
        });
    }

    [LibraryImport(Library)]
    public static partial IntPtr mpv_create();

    [LibraryImport(Library)]
    public static partial int mpv_initialize(IntPtr handle);

    [LibraryImport(Library)]
    public static partial void mpv_terminate_destroy(IntPtr handle);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_option_string(IntPtr handle, string name, string value);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_property_string(IntPtr handle, string name, string value);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr mpv_get_property_string(IntPtr handle, string name);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_get_property(IntPtr handle, string name, MpvFormat format, void* data);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_observe_property(IntPtr handle, ulong replyUserdata, string name, MpvFormat format);

    [LibraryImport(Library)]
    public static partial int mpv_command(IntPtr handle, IntPtr* args);

    [LibraryImport(Library)]
    public static partial int mpv_command_async(IntPtr handle, ulong replyUserdata, IntPtr* args);

    [LibraryImport(Library)]
    public static partial MpvEvent* mpv_wait_event(IntPtr handle, double timeout);

    [LibraryImport(Library)]
    public static partial void mpv_wakeup(IntPtr handle);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_request_log_messages(IntPtr handle, string minLevel);

    [LibraryImport(Library)]
    public static partial IntPtr mpv_error_string(int error);

    [LibraryImport(Library)]
    public static partial void mpv_free(IntPtr data);

    [LibraryImport(Library)]
    public static partial int mpv_render_context_create(IntPtr* result, IntPtr handle, MpvRenderParam* parameters);

    [LibraryImport(Library)]
    public static partial void mpv_render_context_set_update_callback(IntPtr context, delegate* unmanaged<IntPtr, void> callback, IntPtr callbackContext);

    [LibraryImport(Library)]
    public static partial ulong mpv_render_context_update(IntPtr context);

    [LibraryImport(Library)]
    public static partial int mpv_render_context_render(IntPtr context, MpvRenderParam* parameters);

    [LibraryImport(Library)]
    public static partial void mpv_render_context_free(IntPtr context);

    public static string ErrorString(int error) => Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? $"mpv error {error}";
}

public sealed class MpvException(string message, int error = 0) : Exception(message)
{
    /// <summary>mpv's error code (negative), or 0 when the failure wasn't one of mpv's calls.</summary>
    public int Error { get; } = error;

    internal static void Check(int result, string what)
    {
        if (result < 0) throw new MpvException($"{what}: {MpvNative.ErrorString(result)}", result);
    }
}
