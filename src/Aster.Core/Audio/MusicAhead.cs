using Aster.Core.Jellyfin;

namespace Aster.Core.Audio;

/// <summary>
/// Save upcoming songs (Settings ▸ Music), as the Apple app's: the next
/// songs in the queue saved one after another, once the song playing is
/// under way, so they start at once and play on if the connection drops.
/// Kept in Aster's own folder, and let go once heard (or no longer coming
/// up); what a run left is cleared as the next starts.
/// </summary>
public sealed class MusicAhead : IDisposable
{
    /// <summary>How many songs ahead are saved.</summary>
    public const int Ahead = 5;

    private readonly string folder;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly Dictionary<string, string> saved = new(StringComparer.Ordinal);
    private CancellationTokenSource? running;

    public MusicAhead(string? folder = null)
    {
        this.folder = folder ?? Path.Combine(AppPaths.Data, "Upcoming");
        try
        {
            if (Directory.Exists(this.folder)) Directory.Delete(this.folder, recursive: true);
            Directory.CreateDirectory(this.folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The song's saved file, if it's been saved whole.</summary>
    public string? SavedFile(string itemId)
    {
        lock (saved) return saved.TryGetValue(itemId, out var file) && File.Exists(file) ? file : null;
    }

    /// <summary>
    /// The songs coming up, in order, each with where to fetch it from: those
    /// not saved yet are, one at a time; any saved that aren't coming up any
    /// more (or are <paramref name="playing"/>'s predecessors) are let go.
    /// </summary>
    public void Want(IReadOnlyList<(string Id, Uri Url)> upcoming, string? playing)
    {
        running?.Cancel();
        var keep = upcoming.Take(Ahead).Select(u => u.Id).Append(playing).OfType<string>().ToHashSet(StringComparer.Ordinal);
        LetGo(id => !keep.Contains(id));
        var missing = upcoming.Take(Ahead).Where(u => SavedFile(u.Id) is null).ToList();
        if (missing.Count == 0) return;
        var cancel = running = new CancellationTokenSource();
        _ = Task.Run(() => SaveAsync(missing, cancel.Token));
    }

    /// <summary>Everything saved let go, and any saving stopped.</summary>
    public void Clear()
    {
        running?.Cancel();
        LetGo(_ => true);
    }

    private async Task SaveAsync(IReadOnlyList<(string Id, Uri Url)> songs, CancellationToken ct)
    {
        foreach (var (id, url) in songs)
        {
            if (ct.IsCancellationRequested) return;
            var file = Path.Combine(folder, id);
            var part = file + ".part";
            try
            {
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) continue;
                    await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var output = File.Create(part);
                    await body.CopyToAsync(output, ct).ConfigureAwait(false);
                }
                File.Move(part, file, overwrite: true);
                lock (saved) saved[id] = file;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                TryDelete(part);
                // No connection: the rest wouldn't come either, until next time.
                if (error is not IOException) return;
            }
        }
    }

    private void LetGo(Func<string, bool> which)
    {
        List<string> files;
        lock (saved)
        {
            var going = saved.Where(p => which(p.Key)).ToList();
            foreach (var (id, _) in going) saved.Remove(id);
            files = going.Select(p => p.Value).ToList();
        }
        foreach (var file in files) TryDelete(file);
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        Clear();
        http.Dispose();
    }
}
