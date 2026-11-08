using System.Diagnostics;
using System.Globalization;
using Aster.Core.Jellyfin;

namespace Aster.Core.Diagnostics;

/// <summary>
/// What a debug report tells of this run: notable moments (each playback
/// attempt, what it played and how, and why it failed), the app's recent
/// trace lines, and a crash, written to a file as the app goes down so the
/// next run can offer to send it. The Android app's DebugLog.
/// </summary>
public static class DebugLog
{
    private const int KeepNotes = 300;
    private const int KeepLines = 1500;

    private static readonly LinkedList<string> notes = new();
    private static readonly LinkedList<string> lines = new();
    private static string crashFile = Path.Combine(AppPaths.Data, "last-crash.txt");
    private static int installed;

    /// <summary>
    /// Sets the crash handler up, and catches the app's trace lines (its own
    /// and Avalonia's warnings) for the report; once, as the app starts.
    /// </summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        Trace.Listeners.Add(new Listener());
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                File.WriteAllText(crashFile,
                    $"{Stamp()} on thread {Thread.CurrentThread.Name ?? Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)}\n{e.ExceptionObject}\n\nBefore it:\n{string.Join("\n", Recent())}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Note("Error", $"A background task failed unseen: {e.Exception.InnerException?.GetType().Name}: {e.Exception.InnerException?.Message}");
    }

    /// <summary>Keeps the crash in another file, for tests.</summary>
    internal static void UseCrashFile(string path) => crashFile = path;

    /// <summary>A moment worth a report's notice; also a trace line.</summary>
    public static void Note(string tag, string message)
    {
        Add(notes, $"{Stamp()} [{tag}] {message}", KeepNotes);
        Trace.WriteLine($"[{tag}] {message}");
    }

    /// <summary>This run's notable moments, oldest first.</summary>
    public static IReadOnlyList<string> Recent() { lock (notes) return [.. notes]; }

    /// <summary>The app's recent trace lines, oldest first.</summary>
    public static IReadOnlyList<string> Lines() { lock (lines) return [.. lines]; }

    /// <summary>What was last played, field by field (container, codecs, how it played, any error), for a report's <c>playback</c>.</summary>
    public static IReadOnlyDictionary<string, string> LastPlayback { get; set; } = new Dictionary<string, string>();

    /// <summary>The crash the last run ended in, if there was one and it hasn't been sent or put aside.</summary>
    public static string? LastCrash()
    {
        try { return File.Exists(crashFile) ? File.ReadAllText(crashFile) : null; }
        catch (IOException) { return null; }
    }

    /// <summary>The crash has been sent, or the viewer chose not to: it isn't offered again.</summary>
    public static void ClearCrash()
    {
        try { File.Delete(crashFile); }
        catch (IOException) { }
    }

    private static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static void Add(LinkedList<string> list, string line, int keep)
    {
        lock (list)
        {
            list.AddLast(line);
            while (list.Count > keep) list.RemoveFirst();
        }
    }

    /// <summary>Trace's lines, kept as they're written; a line written in pieces is kept whole.</summary>
    private sealed class Listener : TraceListener
    {
        private readonly System.Text.StringBuilder pending = new();

        public override void Write(string? message)
        {
            lock (pending) pending.Append(message);
        }

        public override void WriteLine(string? message)
        {
            string line;
            lock (pending)
            {
                line = pending.Append(message).ToString();
                pending.Clear();
            }
            Add(lines, $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {line}", KeepLines);
        }
    }
}
