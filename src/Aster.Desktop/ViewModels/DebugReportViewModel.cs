using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Aster.Core;
using Aster.Core.Diagnostics;
using Aster.Core.Jellyfin;
using Aster.Desktop.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// Send Debug Report, as the Android and Apple apps have it: asks what
/// happened, says what a report holds, sends it, and shows the id the
/// service gave it, to quote in a bug report. With <paramref name="afterCrash"/>
/// it's the offer after the last run ended in a crash, which goes with the
/// report; Not Now puts that crash aside. From the couch, where typing is
/// slow, the words are optional and no reply address is asked for.
/// </summary>
public sealed partial class DebugReportViewModel(INavigator navigator, IReadOnlyList<string> names, bool afterCrash, Action close) : ViewModelBase, IDisposable
{
    public static string AppVersion { get; } =
        typeof(DebugReportViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";

    public bool AfterCrash => afterCrash;
    public bool AsksEmail => !SteamSession.IsCouch;

    public string Heading => Code is not null ? "Report Sent" : afterCrash ? "Aster Closed Unexpectedly" : "Send Debug Report";
    public string DescriptionPlaceholder => SteamSession.IsCouch ? "What happened? (optional)" : "What happened? What you were doing, and what went wrong";
    public string Contents => DebugReport.Contents;
    public string Issues => $"If you open a bug report at {DebugReport.Issues}, quote it there.";
    public string DismissTitle => afterCrash ? "Not Now" : "Cancel";
    public string SendTitle => Failure is not null ? "Try Again" : "Send";
    public bool ShowsForm => Code is null;

    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial string Email { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendTitle))]
    public partial string? Failure { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(DismissCommand))]
    public partial bool IsSending { get; set; }

    /// <summary>The id the service gave the report, once sent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(ShowsForm))]
    public partial string? Code { get; set; }

    private bool Idle() => !IsSending;

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task SendAsync()
    {
        IsSending = true;
        Failure = null;
        try
        {
            var crash = DebugLog.LastCrash();
            var context = new DebugReportContext
            {
                AppVersion = AppVersion,
                ServerKind = navigator.Client.Kind.ToString().ToLowerInvariant(),
                ServerVersion = await ServerVersionAsync(),
                Names = names,
                DeviceLines = DeviceLines(),
            };
            var description = Description.Length > 10_000 ? Description[..10_000] : Description;
            var report = await Task.Run(() => DebugReport.Build(context, description, crash, AsksEmail ? Email : null));
            Code = await DebugReport.SendAsync(report, AppVersion);
            if (crash is not null) DebugLog.ClearCrash();
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            Failure = error is TaskCanceledException or TimeoutException ? "The report service didn't answer. Try again in a moment." : error.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private void Dismiss()
    {
        if (afterCrash && Code is null) DebugLog.ClearCrash();
        close();
    }

    [RelayCommand] private void Done() => close();

    /// <summary>Left by Back (Escape, a controller's B): for the crash offer, the same as Not Now.</summary>
    public void Dispose()
    {
        if (afterCrash && Code is null && !IsSending) DebugLog.ClearCrash();
    }

    /// <summary>The server's version, if it answers within a few seconds.</summary>
    private async Task<string?> ServerVersionAsync()
    {
        if (navigator.Jellyfin is not { } jellyfin) return null;
        try { return (await jellyfin.PublicSystemInfoAsync().WaitAsync(TimeSpan.FromSeconds(5))).Version; }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { return null; }
    }

    /// <summary>The window, Steam, and the video settings that bear on playback.</summary>
    private static IReadOnlyList<string> DeviceLines()
    {
        var lines = new List<string> { SteamSession.Describe() + $" couch={SteamSession.IsCouch}" };
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            lines.Add($"Window: {window.ClientSize.Width:0}x{window.ClientSize.Height:0} at scale {window.RenderScaling:0.##}, {window.WindowState}");
        lines.Add($"Session: {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "?"}, desktop {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "?"}");
        lines.Add($"Renderer: {(Environment.GetEnvironmentVariable("ASTER_GLX") is null ? "EGL first" : "GLX")}");
        return lines;
    }
}
