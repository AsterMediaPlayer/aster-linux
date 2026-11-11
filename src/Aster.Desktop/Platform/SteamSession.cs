using System;

namespace Aster.Desktop.Platform;

/// <summary>
/// Where the app was started: from Steam (a game shortcut, Big Picture,
/// a Steam Deck's Game Mode), inside gamescope, or on an ordinary desktop.
/// Read once, from what Steam and gamescope put in the environment.
/// </summary>
public static class SteamSession
{
    /// <summary>Started by Steam: it sets these for whatever it launches, a non-Steam shortcut included.</summary>
    public static bool LaunchedBySteam { get; } =
        Has("SteamGameId") || Has("SteamAppId") || Has("SteamClientLaunch") || Has("STEAM_COMPAT_APP_ID");

    /// <summary>
    /// Inside gamescope: a Steam Deck's Game Mode, a Steam Machine-style
    /// session, or gamescope run by hand. It has one window, full screen, so
    /// the app should be too.
    /// </summary>
    public static bool InGamescope { get; } =
        Has("GAMESCOPE_WAYLAND_DISPLAY")
        || string.Equals(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), "gamescope", StringComparison.OrdinalIgnoreCase);

    /// <summary>Steam's controller-first interface (Big Picture, Game Mode) launched it.</summary>
    public static bool InGamepadUi { get; } = Has("SteamGamepadUI") || Has("SteamTenfoot");

    /// <summary>
    /// Plasma Bigscreen, KDE's TV session: Plasma started with its Bigscreen
    /// shell. It reports focus as any Plasma does, so the app's controllers
    /// can be let go of when it's behind (Bigscreen needs them back for its own menus).
    /// </summary>
    public static bool InBigscreen { get; } =
        string.Equals(Environment.GetEnvironmentVariable("PLASMA_DEFAULT_SHELL"), "org.kde.plasma.bigscreen", StringComparison.Ordinal);

    /// <summary>A Steam Deck.</summary>
    public static bool OnSteamDeck { get; } = Environment.GetEnvironmentVariable("SteamDeck") == "1";

    /// <summary>
    /// Watched from the couch with a controller: the window is full screen
    /// from the start and stays so, and the sidebar has an Exit. On by itself
    /// whenever Steam starts the app (its desktop Big Picture doesn't say it's
    /// Big Picture, so launched by Steam at all counts), in gamescope and in
    /// Plasma Bigscreen; <c>--couch</c> asks for it anywhere, <c>--desktop</c> turns it off.
    /// </summary>
    public static bool Couch(string[] args) =>
        IsCouch = Array.IndexOf(args, "--desktop") < 0
            && (Array.IndexOf(args, "--couch") >= 0 || LaunchedBySteam || InGamescope || InGamepadUi || InBigscreen);

    /// <summary>What <see cref="Couch"/> found at launch: the pages a controller needs (the search keyboard) show.</summary>
    public static bool IsCouch { get; private set; }

    public static string Describe() =>
        $"Steam: launched={LaunchedBySteam} gamescope={InGamescope} gamepadUi={InGamepadUi} deck={OnSteamDeck} bigscreen={InBigscreen}";

    private static bool Has(string name) => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name));
}
