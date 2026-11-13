// Continue Watching on the Plasma Bigscreen home screen. Bigscreen has no way
// for an app to add a row, so Aster puts a patched copy of the installed home
// screen where Plasma looks first (~/.local/share/plasma/plasmoids), made from
// whatever version is installed: every change is pinned to text that must be
// there exactly once, and a version they don't fit is left alone. A marker
// says the copy is Aster's; anyone else's copy is never touched.

using System.Diagnostics;
using System.Text.Json;

namespace Aster.Core.Integration;

public enum BigscreenState
{
    /// <summary>Not Linux, or Plasma Bigscreen isn't installed.</summary>
    NotAvailable,
    NotInstalled,
    Installed,
    /// <summary>Someone else's home screen copy is in place; Aster leaves it.</summary>
    OtherCopy,
}

public static class BigscreenInstall
{
    /// <summary>Raised when the patches change, so an older install is redone.</summary>
    private const int PatchVersion = 1;
    private const string Plasmoid = "org.kde.bigscreen.homescreen";
    private const string Marker = ".aster-installed.json";

    private static string SystemDir => $"/usr/share/plasma/plasmoids/{Plasmoid}";

    private static string UserDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
        "plasma", "plasmoids", Plasmoid);

    /// <summary>What the home screen runs to reach Aster: the AppImage itself when it is one.</summary>
    private static string AsterCommand =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image ? image : Environment.ProcessPath ?? "aster";

    public static BigscreenState State
    {
        get
        {
            if (!OperatingSystem.IsLinux() || !File.Exists(Path.Combine(SystemDir, "metadata.json"))) return BigscreenState.NotAvailable;
            if (File.Exists(Path.Combine(UserDir, Marker))) return BigscreenState.Installed;
            return Directory.Exists(UserDir) ? BigscreenState.OtherCopy : BigscreenState.NotInstalled;
        }
    }

    /// <summary>
    /// The installed home screen's QML, hashed: its metadata carries no
    /// version, and this changes with any update that could move the row.
    /// </summary>
    public static string? Fingerprint
    {
        get
        {
            try
            {
                var ui = Path.Combine(SystemDir, "contents", "ui");
                using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                foreach (var file in Directory.GetFiles(ui, "*.qml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                {
                    hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(ui, file) + "\n"));
                    hash.AppendData(File.ReadAllBytes(file));
                }
                return Convert.ToHexString(hash.GetHashAndReset())[..16];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>The installed Bigscreen's version ("6.8.2"), from its metadata, for messages; often not given.</summary>
    public static string? BigscreenVersion
    {
        get
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(SystemDir, "metadata.json")));
                return doc.RootElement.GetProperty("KPlugin").GetProperty("Version").GetString();
            }
            catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
        }
    }

    /// <summary>
    /// Puts the row on the home screen and restarts it. Null when done; else
    /// why not (this Bigscreen version changed where the row goes, or
    /// someone else's copy is in place).
    /// </summary>
    public static string? Install(bool restartShell = true)
    {
        var state = State;
        if (state == BigscreenState.NotAvailable) return "Plasma Bigscreen isn't installed.";
        if (state == BigscreenState.OtherCopy) return "Another customized Bigscreen home screen is installed, so Aster leaves it alone.";

        var staging = UserDir + ".aster-new";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            CopyTree(SystemDir, staging);
            var ui = Path.Combine(staging, "contents", "ui");
            foreach (var (file, edits) in Patches())
            {
                var path = Path.Combine(ui, file);
                var text = File.ReadAllText(path);
                foreach (var (anchor, replacement) in edits)
                {
                    var at = text.IndexOf(anchor, StringComparison.Ordinal);
                    if (at < 0 || text.IndexOf(anchor, at + anchor.Length, StringComparison.Ordinal) >= 0)
                        return BigscreenVersion is { } version ? $"Plasma Bigscreen {version} isn't supported yet." : "This version of Plasma Bigscreen isn't supported yet.";
                    text = text[..at] + replacement + text[(at + anchor.Length)..];
                }
                File.WriteAllText(path, text);
            }
            File.WriteAllText(Path.Combine(ui, "launcher", "delegates", "ContinueDelegate.qml"), Fill(ContinueDelegateQml));
            File.WriteAllText(Path.Combine(staging, Marker), JsonSerializer.Serialize(new MarkerFile(PatchVersion, Fingerprint, AsterCommand)));

            if (Directory.Exists(UserDir)) Directory.Delete(UserDir, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(UserDir)!);
            Directory.Move(staging, UserDir);
            RunPostInstallHook();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { }
            return "Couldn't write the home screen: " + error.Message;
        }
        if (restartShell) RestartShell();
        return null;
    }

    /// <summary>Back to Bigscreen's own home screen (only Aster's copy is ever removed).</summary>
    public static void Uninstall(bool restartShell = true)
    {
        if (State != BigscreenState.Installed) return;
        try { Directory.Delete(UserDir, recursive: true); }
        catch (IOException) { return; }
        if (restartShell) RestartShell();
    }

    /// <summary>
    /// At start: Bigscreen updated, Aster moved, or the patches changed since
    /// the install, so it's redone from the new home screen, or taken out
    /// (back to Bigscreen's own) when this version no longer fits.
    /// </summary>
    public static void RepairIfOutdated()
    {
        if (State != BigscreenState.Installed) return;
        MarkerFile? marker;
        try { marker = JsonSerializer.Deserialize<MarkerFile>(File.ReadAllText(Path.Combine(UserDir, Marker))); }
        catch (Exception error) when (error is IOException or JsonException) { marker = null; }
        if (marker is { } m && m.Patch == PatchVersion && m.Bigscreen == Fingerprint && m.Aster == AsterCommand) return;
        try { Directory.Delete(UserDir, recursive: true); }
        catch (IOException) { return; }
        if (Install(restartShell: false) is not null) Trace.WriteLine("Bigscreen: this version doesn't take the Continue Watching row; back to its own home screen");
        RestartShell();
    }

    private sealed record MarkerFile(int Patch, string? Bigscreen, string Aster);

    /// <summary>
    /// ~/.config/aster/bigscreen-post-install, when it's there and can be run:
    /// a user's own changes, put back on top of Aster's after every install and
    /// repair (BIGSCREEN_HOMESCREEN_UI names the home screen's QML folder).
    /// </summary>
    private static void RunPostInstallHook()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var hook = Path.Combine(config, "aster", "bigscreen-post-install");
        if (!File.Exists(hook) || (File.GetUnixFileMode(hook) & UnixFileMode.UserExecute) == 0) return;
        try
        {
            var start = new ProcessStartInfo(hook) { UseShellExecute = false, RedirectStandardError = true };
            start.Environment["BIGSCREEN_HOMESCREEN_UI"] = Path.Combine(UserDir, "contents", "ui");
            using var run = Process.Start(start);
            if (run is null) return;
            var errors = run.StandardError.ReadToEndAsync();
            if (!run.WaitForExit(30_000)) { run.Kill(entireProcessTree: true); Trace.WriteLine("Bigscreen: post-install hook timed out"); return; }
            if (run.ExitCode != 0) Trace.WriteLine($"Bigscreen: post-install hook failed ({run.ExitCode}): {errors.Result}");
        }
        catch (System.ComponentModel.Win32Exception error) { Trace.WriteLine("Bigscreen: post-install hook: " + error.Message); }
    }

    private static void RestartShell()
    {
        try { Process.Start(new ProcessStartInfo("systemctl", "--user restart plasma-plasmashell") { UseShellExecute = false })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    /// <summary>The Aster command, quoted for the shell the home screen runs it through.</summary>
    private static string Fill(string qml) => qml.Replace("@ASTER@", JsonSerializer.Serialize("'" + AsterCommand.Replace("'", "'\\''") + "'"));

    private static IEnumerable<(string File, (string Anchor, string Replacement)[] Edits)> Patches()
    {
        yield return ("launcher/delegates/IconDelegate.qml", [
            ("import QtQuick.Window\n", "import QtQuick.Window\nimport QtQuick.Effects\n"),
            ("    property var iconImage\n", "    property var iconImage\n" + IconDelegateProperties),
            ("            // Long press animation\n", IconDelegateOverlay + "            // Long press animation\n"),
        ]);
        yield return ("launcher/DelegateListView.qml", [
            ("    property real cardAspectRatio: 0.625\n", "    property real cardAspectRatio: 0.625\n" + ListProperties),
            (StockScroll, FixedScroll),
        ]);
        yield return ("launcher/LauncherHome.qml", [
            ("import org.kde.taskmanager as TaskManager\n", "import org.kde.taskmanager as TaskManager\nimport org.kde.plasma.plasma5support as P5Support\n"),
            ("launcherHomeColumn.currentSection !== favAppsView.currentViewDownwards", "launcherHomeColumn.currentSection !== continueView.currentViewDownwards"),
            ("        favAppsView.currentViewDownwards?.forceActiveFocus();\n", "        continueView.currentViewDownwards?.forceActiveFocus();\n"),
            ("        readonly property Item firstSection: favAppsView.currentViewDownwards\n", "        readonly property Item firstSection: continueView.currentViewDownwards\n"),
            ("            property var currentViewUpwards: visible ? favAppsView : root.navigationUp\n", "            property var currentViewUpwards: visible ? favAppsView : continueView.currentViewUpwards\n"),
            ("            navigationUp: root.navigationUp\n            navigationDown: recentView.currentViewDownwards\n",
             "            navigationUp: continueView.currentViewUpwards\n            navigationDown: recentView.currentViewDownwards\n"),
            ("            } else if (firstSection == currentSection) {\n",
             "            } else if (currentSection === continueView) {\n                // Aster: lower, leaving room for its details above\n                return startY + Math.round(root.height * 0.15);\n            } else if (firstSection == currentSection) {\n"),
            ("    Component.onCompleted: activateAppView()\n", Fill(LauncherContinueModel)),
            ("        DelegateListView {\n            id: favAppsView\n", ContinueRow + "        DelegateListView {\n            id: favAppsView\n"),
        ]);
        yield return ("HomeScreen.qml", [
            ("import QtQuick.Window\n", "import QtQuick.Window\nimport QtQuick.Effects\n"),
            ("    HomeHeader {\n        id: header\n", HomeScreenHero + "    HomeHeader {\n        id: header\n"),
        ]);
    }

    private const string IconDelegateProperties = """

    // Aster: a logo over the artwork, watch progress (0-1, -1 for none), and the details box's lines
    property string artworkLogo: ""
    property real progress: -1
    property var details: null

""";

    private const string IconDelegateOverlay = """
            // Aster: dark fade so the logo reads on any art
            Rectangle {
                anchors.fill: parent
                radius: card.radius
                z: 10
                visible: logoItem.visible
                gradient: Gradient {
                    GradientStop { position: 0.25; color: "transparent" }
                    GradientStop { position: 0.65; color: Qt.rgba(0, 0, 0, 0.6) }
                    GradientStop { position: 1.0; color: Qt.rgba(0, 0, 0, 0.9) }
                }
            }

            Image {
                id: logoItem
                z: 11
                anchors {
                    left: parent.left
                    bottom: parent.bottom
                    leftMargin: Math.round(parent.width * 0.06)
                    bottomMargin: Math.round(parent.height * (delegate.progress >= 0 ? 0.17 : 0.08))
                }
                width: Math.round(parent.width * 0.6)
                height: Math.round(parent.height * 0.46)
                fillMode: Image.PreserveAspectFit
                horizontalAlignment: Image.AlignLeft
                verticalAlignment: Image.AlignBottom
                asynchronous: true
                mipmap: true
                sourceSize.width: width * Screen.devicePixelRatio
                source: delegate.artworkLogo.length > 0 ? "file://" + delegate.artworkLogo : ""
                visible: card.status === Image.Ready && status === Image.Ready
                layer.enabled: visible
                layer.effect: MultiEffect {
                    shadowEnabled: true
                    shadowColor: Qt.rgba(0, 0, 0, 0.9)
                    shadowBlur: 0.5
                    shadowVerticalOffset: 2
                }
            }

            // Aster: watch progress
            Rectangle {
                z: 12
                visible: delegate.progress >= 0
                anchors {
                    left: parent.left
                    right: parent.right
                    bottom: parent.bottom
                    leftMargin: Math.round(parent.width * 0.06)
                    rightMargin: Math.round(parent.width * 0.06)
                    bottomMargin: Math.round(parent.height * 0.08)
                }
                height: 6
                radius: 3
                color: Qt.rgba(1, 1, 1, 0.3)
                Rectangle {
                    anchors { left: parent.left; top: parent.top; bottom: parent.bottom }
                    width: parent.width * Math.max(0, Math.min(1, delegate.progress))
                    radius: 3
                    color: Kirigami.Theme.highlightColor
                }
            }

""";

    private const string ListProperties = """

    // Aster: the highlighted tile gets the home screen's logo and details, above the row or under it
    property bool showsDetails: false
    property bool detailsAbove: false

""";

    private const string StockScroll = """
        onCurrentIndexChanged: {
            const maxContentX = Math.max(0, contentWidth - width);
            const currentItemX = itemAtIndex(currentIndex)?.x ?? (originX + currentIndex * cellWidth);
            xAnim.to = Math.max(0, Math.min(currentItemX - cellWidth, maxContentX));
            if (xAnim.to != contentX) xAnim.restart();
        }

""";

    // From the index (cells are one width), not the delegate's x, which is
    // mid-animation while the row's items change; and kept on screen when they do.
    private const string FixedScroll = """
        function scrollToCurrent() {
            if (currentIndex < 0) return;
            const maxContentX = Math.max(0, contentWidth - width);
            const currentItemX = originX + currentIndex * cellWidth;
            xAnim.to = Math.max(originX, Math.min(currentItemX - cellWidth, originX + maxContentX));
            if (xAnim.to != contentX) xAnim.restart();
        }
        onCurrentIndexChanged: scrollToCurrent()
        onCountChanged: Qt.callLater(() => {
            if (currentIndex >= count) currentIndex = count - 1;
            scrollToCurrent();
            if (activeFocus && currentItem && !currentItem.activeFocus) currentItem.forceActiveFocus();
        })
        onWidthChanged: Qt.callLater(scrollToCurrent)

""";

    private const string LauncherContinueModel = """
    Component.onCompleted: {
        refreshContinue();
        activateAppView();
    }

    // Aster: Continue Watching, from whichever server Aster is signed in to
    ListModel { id: continueModel }
    property string continueJson: ""

    function refreshContinue() {
        continueSource.connectSource(@ASTER@ + " --home-screen-row");
    }

    P5Support.DataSource {
        id: continueSource
        engine: "executable"
        connectedSources: []
        onNewData: (source, data) => {
            disconnectSource(source);
            const out = (data["stdout"] || "").trim();
            if (out.length === 0 || out === root.continueJson) return;
            let rows;
            try { rows = JSON.parse(out); } catch (e) { return; }
            root.continueJson = out;
            continueModel.clear();
            for (const row of rows) continueModel.append(row);
        }
    }

    // Back on the home screen: the row catches up straight away
    Connections {
        target: root.Window.window
        function onActiveChanged() {
            if (root.Window.window && root.Window.window.active) root.refreshContinue();
        }
    }
    Timer {
        interval: 5 * 60 * 1000
        repeat: true
        running: true
        onTriggered: root.refreshContinue()
    }

""";

    private const string ContinueRow = """
        DelegateListView {
            id: continueView
            property var currentViewUpwards: visible ? continueView : root.navigationUp
            property var currentViewDownwards: visible ? continueView : favAppsView.currentViewDownwards

            title: i18n("Continue Watching")
            showsDetails: true
            detailsAbove: true
            columnOffset: -1
            cardAspectRatio: 9 / 16
            opacity: root.sectionOpacity(continueView)
            Behavior on opacity { OpacityAnimator { duration: Kirigami.Units.longDuration } }
            model: continueModel
            visible: count > 0
            currentIndex: 0
            focus: visible
            onActiveFocusChanged: if (activeFocus)
                launcherHomeColumn.currentSection = continueView
            delegate: Delegates.ContinueDelegate {
                itemId: model.itemId
                text: model.title
                subtitle: model.subtitle
                artwork: model.backdrop
                artworkLogo: model.logo
                progress: model.progress
                details: ({ line1: model.line1, overview: model.overview, line3: model.line3 })
            }

            navigationUp: root.navigationUp
            navigationDown: favAppsView.currentViewDownwards
        }

""";

    private const string ContinueDelegateQml = """
/*
    Aster: a Continue Watching tile; picking it plays the title in Aster.
    SPDX-License-Identifier: GPL-2.0-or-later
*/

import QtQuick
import org.kde.bigscreen as Bigscreen
import org.kde.plasma.plasmoid

IconDelegate {
    id: delegate

    property string itemId

    icon.name: "video-television"
    useIconColors: false

    onClicked: {
        Bigscreen.NavigationSoundEffects.playClickedSound();
        Bigscreen.NavigationRumble.playConfirmRumble();
        if (!/^[0-9A-Za-z-]{1,64}$/.test(delegate.itemId)) return;
        // Its own scope, so restarting Plasma never takes Aster with it; an
        // Aster already open is handed the title.
        Plasmoid.executeCommand("systemd-run --user --collect --quiet " + @ASTER@ + " --play " + delegate.itemId);
    }
}

""";

    private const string HomeScreenHero = """
    // ---- Aster: the highlighted tile's artwork behind everything, its logo top right, its details ----
    readonly property var focusTile: {
        let it = root.Window.activeFocusItem;
        while (it) {
            if (it.artwork !== undefined) return it;
            if (it.currentItem && it.currentItem.artwork !== undefined) return it.currentItem;
            it = it.parent;
        }
        return null;
    }
    readonly property string focusArt: focusTile ? (focusTile.artwork || "") : ""
    onFocusArtChanged: artLayer.show(focusArt)

    readonly property var focusSection: {
        let p = focusTile ? focusTile.parent : null;
        while (p) {
            if (p.showsDetails !== undefined) return p;
            p = p.parent;
        }
        return null;
    }
    readonly property bool focusInDetailsRow: focusSection !== null && focusSection.showsDetails
    readonly property bool detailsAbove: focusInDetailsRow && focusSection.detailsAbove === true
    readonly property var detailsData: (focusInDetailsRow && focusTile && focusTile.details) ? focusTile.details : null
    readonly property string clearArtPath: (focusInDetailsRow && focusTile && focusTile.artworkLogo) ? focusTile.artworkLogo : ""

    Item {
        id: artLayer
        anchors.fill: parent
        property Image front: artA
        property string pending: ""

        function show(path) {
            pending = path;
            if (path.length === 0) {
                artA.opacity = 0; artB.opacity = 0;
                return;
            }
            const back = (front === artA) ? artB : artA;
            const url = "file://" + path;
            if (back.source == url && back.status === Image.Ready) { reveal(back); return; }
            back.source = url;
        }
        function reveal(img) {
            if (img.source != "file://" + pending) return;
            const other = (img === artA) ? artB : artA;
            img.opacity = 1; other.opacity = 0; front = img;
        }

        component ArtImage: Image {
            anchors.fill: parent
            fillMode: Image.PreserveAspectCrop
            asynchronous: true
            cache: true
            opacity: 0
            sourceSize.width: 1920
            Behavior on opacity { NumberAnimation { duration: 700; easing.type: Easing.InOutQuad } }
            onStatusChanged: if (status === Image.Ready) artLayer.reveal(this)
            layer.enabled: opacity > 0 && implicitWidth > 0 && implicitWidth < 1200
            layer.effect: MultiEffect { blurEnabled: true; blurMax: 48; blur: 0.8 }
        }
        ArtImage { id: artA }
        ArtImage { id: artB }

        Rectangle {
            anchors.fill: parent
            opacity: Math.max(artA.opacity, artB.opacity)
            gradient: Gradient {
                GradientStop { position: 0.0; color: Qt.rgba(0, 0, 0, 0.35) }
                GradientStop { position: 0.45; color: Qt.rgba(0, 0, 0, 0.45) }
                GradientStop { position: 1.0; color: Qt.rgba(0, 0, 0, 0.85) }
            }
        }
    }

    Image {
        id: clearArt
        anchors {
            top: parent.top
            right: parent.right
            topMargin: header.shrunkHeight + Kirigami.Units.gridUnit
            rightMargin: root.rightMargin
        }
        width: Kirigami.Units.gridUnit * 26
        height: Kirigami.Units.gridUnit * 10
        fillMode: Image.PreserveAspectFit
        horizontalAlignment: Image.AlignRight
        asynchronous: true
        mipmap: true
        source: root.clearArtPath.length > 0 ? "file://" + root.clearArtPath : ""
        opacity: (root.clearArtPath.length > 0 && status === Image.Ready) ? 1 : 0
        Behavior on opacity { NumberAnimation { duration: 400 } }
        layer.enabled: opacity > 0
        layer.effect: MultiEffect {
            shadowEnabled: true
            shadowColor: Qt.rgba(0, 0, 0, 0.85)
            shadowBlur: 0.7
            shadowVerticalOffset: 3
        }
    }

    Rectangle {
        id: details
        readonly property bool active: root.detailsData !== null
        property real posY: 0
        x: root.leftMargin
        y: posY
        z: 5
        width: Math.min(root.width - root.leftMargin - root.rightMargin, Kirigami.Units.gridUnit * 46)
        height: detailsCol.implicitHeight + Kirigami.Units.gridUnit * 1.5
        radius: Kirigami.Units.cornerRadius * 2
        color: root.detailsAbove ? "transparent" : Qt.rgba(0, 0, 0, 0.55)
        border.width: root.detailsAbove ? 0 : 1
        border.color: Qt.rgba(1, 1, 1, 0.08)
        opacity: active ? 1 : 0
        visible: opacity > 0
        Behavior on opacity { NumberAnimation { duration: 300 } }

        Timer {
            interval: 50; repeat: true; triggeredOnStart: true
            running: details.active
            onTriggered: {
                const t = root.focusTile;
                if (!t) return;
                const sec = root.focusSection;
                if (root.detailsAbove && sec) {
                    details.posY = sec.mapToItem(root, 0, 0).y - details.height - Kirigami.Units.gridUnit * 0.5;
                } else {
                    const pt = t.mapToItem(root, 0, 0);
                    details.posY = pt.y + (t.cardHeight || t.height) + Kirigami.Units.gridUnit * 3.4;
                }
            }
        }

        ColumnLayout {
            id: detailsCol
            anchors { left: parent.left; right: parent.right; top: parent.top; margins: Kirigami.Units.gridUnit * 0.75 }
            spacing: Kirigami.Units.smallSpacing

            Controls.Label {
                Layout.fillWidth: true
                text: root.detailsData ? (root.detailsData.line1 || "") : ""
                visible: text.length > 0
                color: Qt.rgba(1, 1, 1, 0.75)
                font.pixelSize: root.detailsAbove ? 20 : 16
                font.weight: root.detailsAbove ? Font.DemiBold : Font.Normal
                elide: Text.ElideRight
            }
            Controls.Label {
                Layout.fillWidth: true
                text: root.detailsData ? (root.detailsData.overview || "") : ""
                visible: text.length > 0
                color: "white"
                font.pixelSize: root.detailsAbove ? 21 : 18
                wrapMode: Text.WordWrap
                maximumLineCount: 3
                elide: Text.ElideRight
            }
            Controls.Label {
                Layout.fillWidth: true
                text: root.detailsData ? (root.detailsData.line3 || "") : ""
                visible: text.length > 0
                color: Kirigami.Theme.highlightColor
                font.pixelSize: root.detailsAbove ? 17 : 15
                elide: Text.ElideRight
            }
        }
    }

""";
}
