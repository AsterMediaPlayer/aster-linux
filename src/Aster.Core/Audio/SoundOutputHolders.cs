using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Aster.Core.Audio;

/// <summary>
/// On Linux, who else is on the sound output that passthrough needs. PipeWire
/// puts a Dolby or DTS stream only on an output nothing else is linked to:
/// another app playing there, or only watching its level (PulseAudio Volume
/// Control's meters do), leaves the stream nowhere to go. Asked of PipeWire
/// itself (<c>pw-dump</c>), so the viewer can be told which app it is.
/// </summary>
public static class SoundOutputHolders
{
    /// <summary>
    /// The names of the other apps linked to an output that takes passthrough;
    /// empty when there are none, or when PipeWire couldn't be asked (not Linux,
    /// no <c>pw-dump</c>, no answer within two seconds).
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindAsync()
    {
        if (!OperatingSystem.IsLinux()) return [];
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var process = Process.Start(new ProcessStartInfo("pw-dump")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null) return [];
            var json = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return Parse(json, Environment.ProcessId);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or OperationCanceledException or JsonException or InvalidOperationException)
        {
            Trace.WriteLine($"Sound output: couldn't ask PipeWire who's on it ({error.Message})");
            return [];
        }
    }

    /// <summary>From <c>pw-dump</c>'s objects: who, other than process <paramref name="self"/>, is linked to a passthrough output.</summary>
    internal static IReadOnlyList<string> Parse(string json, int self)
    {
        using var document = JsonDocument.Parse(json);
        var props = new Dictionary<int, JsonElement>();
        var links = new List<(int From, int To)>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) || !item.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) continue;
            var id = idElement.GetInt32();
            var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (type.EndsWith(":Link", StringComparison.Ordinal))
            {
                if (info.TryGetProperty("output-node-id", out var from) && info.TryGetProperty("input-node-id", out var to))
                    links.Add((from.GetInt32(), to.GetInt32()));
            }
            else if (info.TryGetProperty("props", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                props[id] = p;
            }
        }

        string? Text(JsonElement p, string name) =>
            p.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

        // Outputs that take passthrough: sinks offering more than plain PCM.
        var outputs = props
            .Where(p => Text(p.Value, "media.class") == "Audio/Sink"
                && Text(p.Value, "iec958.codecs") is { } codecs && codecs.Replace("\"PCM\"", "", StringComparison.Ordinal).Contains('"'))
            .Select(p => p.Key)
            .ToHashSet();

        var names = new List<string>();
        foreach (var (from, to) in links)
        {
            // A stream played to the output, or one listening to its monitor.
            var other = outputs.Contains(to) ? from : outputs.Contains(from) ? to : -1;
            if (other < 0 || !props.TryGetValue(other, out var node)) continue;
            var client = Text(node, "client.id") is { } c && int.TryParse(c, out var clientId) && props.TryGetValue(clientId, out var cp) ? cp : (JsonElement?)null;
            var pid = Text(node, "application.process.id") ?? (client is { } cl ? Text(cl, "application.process.id") : null);
            if (pid == self.ToString(System.Globalization.CultureInfo.InvariantCulture)) continue;
            var name = Text(node, "application.name") ?? (client is { } cn ? Text(cn, "application.name") : null) ?? Text(node, "node.name");
            if (name is { Length: > 0 } && !names.Contains(name)) names.Add(name);
        }
        return names;
    }
}
