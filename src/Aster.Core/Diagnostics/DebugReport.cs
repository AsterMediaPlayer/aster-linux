using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aster.Core.Diagnostics;

/// <summary>What the app knows of where it's running, for a report: filled in by the app, which knows the window and the session.</summary>
public sealed record DebugReportContext
{
    public required string AppVersion { get; init; }

    /// <summary>The server's kind ("jellyfin", "emby", "plex"), or null when signed out.</summary>
    public string? ServerKind { get; init; }

    public string? ServerVersion { get; init; }

    /// <summary>The user's name and id and the server's name: taken out wherever they appear.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    /// <summary>Lines for the report's "Device" section: the screen, mpv, Steam, the controllers.</summary>
    public IReadOnlyList<string> DeviceLines { get; init; } = [];
}

/// <summary>
/// A bug report, in the shape Aster's report service takes (POST
/// /api/bug-reports, answered 201 {"id": …}), the same as the Android and
/// Apple apps send: what happened in the viewer's words, the app and
/// computer, the server's kind and version, what was last played, the
/// app's settings, and a log of this run's notable moments, the last crash
/// and the app's recent trace lines. Everything but the viewer's own words
/// passes through <see cref="Redact"/>, so sign-ins, addresses, names, ids
/// and library file names stay behind.
/// </summary>
public static class DebugReport
{
    public static readonly Uri Endpoint = new("https://astermediaplayer.com/api/bug-reports");

    /// <summary>The service takes up to 1 MB of logs; the newest are kept.</summary>
    private const int MaxLogs = 1_000_000;
    private const int MaxDescription = 10_000;

    /// <summary>What goes in it, said to the viewer before it's sent.</summary>
    public const string Contents =
        "It holds the app's version, this computer's model and what it can play, your server's kind and version, what was last " +
        "played, the app's settings, and the app's recent log. Server addresses, sign-ins, passwords, your name and your " +
        "library's file names are taken out before it leaves this computer.";

    /// <summary>Where a report's code is quoted.</summary>
    public static string Issues => OperatingSystem.IsWindows() ? "github.com/AsterMediaPlayer/aster-windows/issues" : "github.com/AsterMediaPlayer/aster-linux/issues";

    public static string Platform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>Settings that hold a place, a name or a sign-in, or what was seen where: never sent.</summary>
    private static readonly string[] Withheld = ["url", "address", "deviceid", "server", "user", "token", "secret", "password", "library", "libraries", "welcome", "seerr", "mdblist", "tracker", "sonos", "speaker", "chosen"];

    /// <summary>The report. <paramref name="description"/> is the viewer's own account; <paramref name="contactEmail"/> an address they chose to give.</summary>
    public static JsonObject Build(DebugReportContext context, string description, string? crash, string? contactEmail = null, AppSettings? settings = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Clean(string s) => Redact.Text(s, context.Names, home);

        var words = description.Trim();
        var report = new JsonObject
        {
            ["description"] = Truncate(words.Length == 0 ? crash is not null ? "The app closed unexpectedly." : "Sent from Settings > About." : words, MaxDescription),
            ["app"] = new JsonObject
            {
                ["version"] = context.AppVersion,
                ["build"] = context.AppVersion,
                ["platform"] = Platform,
                ["os"] = Clean(OperatingSystemName()),
                ["device"] = Clean(DeviceModel()),
            },
        };
        if (context.ServerKind is { } kind)
        {
            var server = new JsonObject { ["type"] = kind };
            if (context.ServerVersion is { } version) server["version"] = version;
            report["server"] = server;
        }
        if (DebugLog.LastPlayback is { Count: > 0 } played)
        {
            var playback = new JsonObject();
            foreach (var (key, value) in played) playback[key] = Clean(value);
            report["playback"] = playback;
        }
        var fields = new JsonObject();
        foreach (var (key, value) in Settings(settings ?? AppSettings.Current)) fields[key] = Clean(value);
        report["settings"] = fields;
        if (contactEmail?.Trim() is { Length: > 0 } email) report["contactEmail"] = email;
        var logs = Clean(Logs(context, crash));
        report["logs"] = logs.Length > MaxLogs ? logs[^MaxLogs..] : logs;
        return report;
    }

    /// <summary>The settings by name, as text, less any that hold a place, a name or a sign-in (lists are left out too).</summary>
    public static IReadOnlyDictionary<string, string> Settings(AppSettings settings)
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var element = JsonSerializer.SerializeToElement(settings, Jellyfin.JellyfinJson.Options);
        foreach (var property in element.EnumerateObject())
        {
            if (Withheld.Any(w => property.Name.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            var value = property.Value;
            switch (value.ValueKind)
            {
                case JsonValueKind.True: found[property.Name] = "true"; break;
                case JsonValueKind.False: found[property.Name] = "false"; break;
                case JsonValueKind.Number: found[property.Name] = value.GetRawText(); break;
                case JsonValueKind.String when value.GetString() is { Length: <= 80 } text: found[property.Name] = text; break;
            }
        }
        return found;
    }

    private static string Logs(DebugReportContext context, string? crash)
    {
        var output = new System.Text.StringBuilder();
        output.AppendLine("== Device");
        output.AppendLine($"Runtime: .NET {Environment.Version}, {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} processors");
        foreach (var line in context.DeviceLines) output.AppendLine(line);
        if (crash is not null)
        {
            output.AppendLine();
            output.AppendLine("== Crash (last run)");
            output.AppendLine(crash);
        }
        output.AppendLine();
        output.AppendLine("== This run");
        foreach (var line in DebugLog.Recent()) output.AppendLine(line);
        output.AppendLine();
        output.AppendLine("== App log");
        foreach (var line in DebugLog.Lines()) output.AppendLine(line);
        return output.ToString();
    }

    /// <summary>"Arch Linux (Linux 6.18.5)", "Microsoft Windows 10.0.26100".</summary>
    private static string OperatingSystemName()
    {
        var kernel = RuntimeInformation.OSDescription;
        if (!OperatingSystem.IsLinux()) return kernel;
        try
        {
            var pretty = File.ReadLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
            if (pretty is not null) return $"{pretty["PRETTY_NAME=".Length..].Trim('"')} ({kernel})";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return kernel;
    }

    /// <summary>The computer's maker and model, from its firmware ("Valve Jupiter" is a Steam Deck); "PC" where it isn't told.</summary>
    private static string DeviceModel()
    {
        if (!OperatingSystem.IsLinux()) return "PC";
        static string? Read(string name)
        {
            try { return File.ReadAllText($"/sys/devices/virtual/dmi/id/{name}").Trim() is { Length: > 0 } text ? text : null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        var model = string.Join(" ", new[] { Read("sys_vendor"), Read("product_name") }.OfType<string>());
        return model.Length > 0 ? model : "PC";
    }

    private static string Truncate(string text, int length) => text.Length > length ? text[..length] : text;

    /// <summary>Sends <paramref name="report"/>; the id the service gave it.</summary>
    public static async Task<string> SendAsync(JsonObject report, string appVersion, HttpClient? http = null, CancellationToken cancel = default)
    {
        var client = http ?? Http.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(report) };
        request.Headers.UserAgent.ParseAdd($"Aster-for-{Platform}/{appVersion}");
        using var response = await client.SendAsync(request, cancel);
        var body = await response.Content.ReadAsStringAsync(cancel);
        JsonObject? answer = null;
        try { answer = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { }
        if (!response.IsSuccessStatusCode)
        {
            var message = answer?["error"]?.GetValueKind() == JsonValueKind.String ? answer["error"]!.GetValue<string>() : null;
            throw new HttpRequestException(string.IsNullOrEmpty(message) ? $"The report service answered {(int)response.StatusCode}." : $"The report service said: {message}");
        }
        return answer?["id"]?.GetValueKind() == JsonValueKind.String && answer["id"]!.GetValue<string>() is { Length: > 0 } id
            ? id
            : throw new HttpRequestException("The report service didn't give the report's id.");
    }

    private static readonly Lazy<HttpClient> Http = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
}
