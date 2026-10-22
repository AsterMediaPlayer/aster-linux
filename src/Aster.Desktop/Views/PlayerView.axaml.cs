using System;
using System.Linq;
using Aster.Core.Channels;
using Aster.Desktop.ViewModels;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Aster.Desktop.Views;

/// <summary>
/// The player's keys, mouse and full screen. Space or K pauses, ← and →
/// (or J and L) skip 10 seconds, ↑ and ↓ change the volume and M mutes, A
/// and S step through the audio and subtitle tracks, F or a double-click
/// toggles full screen, Enter takes the skip on offer, Page Up and Page Down
/// change chapter (or channel), Esc leaves full screen and then the player.
/// </summary>
public partial class PlayerView : UserControl
{
    private WindowState stateBeforeFullScreen = WindowState.Normal;

    public PlayerView()
    {
        InitializeComponent();
        SeekBar.AddHandler(Thumb.DragStartedEvent, (_, _) =>
        {
            if (Model is not { } m) return;
            m.IsScrubbing = true;
            m.PreviewAt(SeekBar.Value);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        SeekBar.AddHandler(Thumb.DragCompletedEvent, (_, _) =>
        {
            if (Model is not { } m) return;
            m.IsScrubbing = false;
            m.PreviewAt(null);
            m.SeekTo(SeekBar.Value);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        // Resting on the bar previews that point; a drag previews where the thumb is (in the model).
        SeekBar.PointerMoved += (_, e) =>
        {
            if (Model is not { IsScrubbing: false, Duration: > 0 } m || TrackSpan() is not { } span) return;
            m.PreviewAt(Math.Clamp((e.GetPosition(PreviewLayer).X - span.Start) / span.Length, 0, 1) * m.Duration);
        };
        SeekBar.PointerExited += (_, _) => { if (Model is { IsScrubbing: false } m) m.PreviewAt(null); };
        PreviewBubble.SizeChanged += (_, _) => PlacePreview();
        FullScreenButton.Click += (_, _) => ToggleFullScreen();
        BugLayer.SizeChanged += (_, _) => PlaceBug();
        DataContextChanged += (_, _) =>
        {
            if (Model is not { } model) return;
            model.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName is nameof(PlayerViewModel.VideoAspect) or nameof(PlayerViewModel.BugCorner) or nameof(PlayerViewModel.BugWidth)) PlaceBug();
                if (change.PropertyName is nameof(PlayerViewModel.Chapters) or nameof(PlayerViewModel.Duration)) MarkChapters();
                if (change.PropertyName is nameof(PlayerViewModel.PreviewSeconds)) PlacePreview();
            };
        };
        Surface.PointerMoved += (_, _) => Model?.PokeControls();
        Surface.DoubleTapped += (_, e) =>
        {
            // Only on the picture, not on a control.
            if (e.Source is Visual source && source.FindAncestorOfType<Button>() is null && source.FindAncestorOfType<Slider>() is null) ToggleFullScreen();
        };
        // The controls stay up while a menu is open.
        foreach (var node in this.GetLogicalDescendants())
        {
            if (node is Button { Flyout: { } flyout })
            {
                flyout.Opened += (_, _) => { if (Model is { } m) m.OpenMenus++; };
                flyout.Closed += (_, _) =>
                {
                    if (Model is not { } m) return;
                    m.OpenMenus = Math.Max(0, m.OpenMenus - 1);
                    m.PokeControls();
                    Focus();
                };
            }
        }
    }

    private PlayerViewModel? Model => DataContext as PlayerViewModel;

    /// <summary>
    /// The bug in its corner of the picture itself, so a 4:3 show between
    /// black bars keeps it on the picture: in from the picture's edges by a
    /// share of its size, as on the Apple TV.
    /// </summary>
    private void PlaceBug()
    {
        if (Model is not { } model) return;
        var (width, height) = (BugLayer.Bounds.Width, BugLayer.Bounds.Height);
        if (width <= 0 || height <= 0) return;
        var (left, top, pictureWidth, pictureHeight) = (0.0, 0.0, width, height);
        var aspect = model.VideoAspect;
        if (aspect > 0 && double.IsFinite(aspect))
        {
            if (width / height > aspect)
            {
                pictureWidth = height * aspect;
                left = (width - pictureWidth) / 2;
            }
            else
            {
                pictureHeight = width / aspect;
                top = (height - pictureHeight) / 2;
            }
        }
        var size = model.BugWidth;
        var x = model.BugCorner.IsLeft ? left + pictureWidth * 0.094 : left + pictureWidth * (1 - 0.094) - size;
        var y = model.BugCorner.IsTop ? top + pictureHeight * 0.12 : top + pictureHeight * (1 - 0.12) - size;
        Canvas.SetLeft(Bug, x);
        Canvas.SetTop(Bug, y);
    }

    /// <summary>A mark under the timeline at each chapter after the first.</summary>
    private void MarkChapters()
    {
        if (Model is not { } model) return;
        var starts = model.Chapters
            .Select(chapter => chapter.StartPositionTicks / 10_000_000.0)
            .Where(seconds => seconds > 0 && seconds < model.Duration)
            .ToList();
        SeekBar.Ticks = starts.Count > 0 ? new AvaloniaList<double>(starts) : null;
        SeekBar.TickPlacement = starts.Count > 0 ? TickPlacement.BottomRight : TickPlacement.None;
    }

    /// <summary>Where the thumb's centre travels along the bar, in the preview layer's terms, and the bar's middle.</summary>
    private readonly record struct BarSpan(double Start, double Length, double Middle);

    private BarSpan? TrackSpan()
    {
        if (SeekBar.GetVisualDescendants().OfType<Track>().FirstOrDefault() is not { } track) return null;
        var thumb = track.Thumb?.Bounds.Width ?? 0;
        var length = track.Bounds.Width - thumb;
        if (length <= 0 || track.TranslatePoint(new Point(thumb / 2, track.Bounds.Height / 2), PreviewLayer) is not { } origin) return null;
        return new BarSpan(origin.X, length, origin.Y);
    }

    /// <summary>The preview above its point on the bar, kept within the bar's ends.</summary>
    private void PlacePreview()
    {
        if (Model is not { PreviewSeconds: { } seconds, Duration: > 0 } model || TrackSpan() is not { } span) return;
        var x = span.Start + span.Length * Math.Clamp(seconds / model.Duration, 0, 1);
        var (width, height) = (PreviewBubble.Bounds.Width, PreviewBubble.Bounds.Height);
        Canvas.SetLeft(PreviewBubble, Math.Clamp(x - width / 2, 0, Math.Max(PreviewLayer.Bounds.Width - width, 0)));
        Canvas.SetTop(PreviewBubble, span.Middle - 20 - height);
    }

    private Window? Window => TopLevel.GetTopLevel(this) as Window;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Window?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Focus();
        // Settings' "Play videos full screen"; the window comes back as the player closes.
        if (Aster.Core.AppSettings.Current.FullScreenPlayback && Window is { WindowState: not WindowState.FullScreen }) ToggleFullScreen();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Window?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        if (Window is { WindowState: WindowState.FullScreen } window) window.WindowState = stateBeforeFullScreen;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// The player's keys, heard at the window before anything else, so they
    /// work wherever the keyboard focus was left (on the button that started
    /// playback, say, on the page now hidden behind).
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model || e.Handled) return;
        // The channel row has the arrows while it's open.
        if (model.Flick is { } flick)
        {
            switch (e.Key)
            {
                case Key.Left: flick.Move(-1); model.FlickActivity(); break;
                case Key.Right: flick.Move(1); model.FlickActivity(); break;
                case Key.Up: flick.PickGoTo(true); model.FlickActivity(); break;
                case Key.Down when flick.IsOnGoTo: flick.PickGoTo(false); model.FlickActivity(); break;
                case Key.Down or Key.Escape or Key.BrowserBack: model.CloseFlick(); break;
                case Key.Enter or Key.Space: model.ChooseFromFlick(); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Space or Key.K or Key.MediaPlayPause:
                model.TogglePauseCommand.Execute(null);
                break;
            case Key.Left or Key.J:
                model.SeekBy(-10);
                break;
            case Key.Right or Key.L:
                model.SeekBy(10);
                break;
            case Key.Enter when model.ShowsSkip:
                model.SkipCommand.Execute(null);
                break;
            // A remote's OK, or a controller's A: pause, as clicking the Apple TV remote does.
            case Key.Enter:
                model.TogglePauseCommand.Execute(null);
                break;
            case Key.F:
                ToggleFullScreen();
                break;
            case Key.A:
                model.CycleAudio();
                break;
            case Key.S or Key.C:
                model.CycleSubtitle();
                break;
            case Key.M or Key.VolumeMute:
                model.ToggleMute();
                break;
            // On a channel, Up opens the channel row, as on the Apple TV.
            case Key.Up when model.OpenFlick():
                // The row over the picture, without the controls coming up under it.
                e.Handled = true;
                return;
            case Key.Up or Key.VolumeUp:
                model.ChangeVolume(5);
                break;
            case Key.Down or Key.VolumeDown:
                model.ChangeVolume(-5);
                break;
            // Channel up and down, on a Channel.
            case Key.PageUp when model.SwitchChannel(1):
                break;
            case Key.PageDown when model.SwitchChannel(-1):
                break;
            // Otherwise the next and previous chapter, as in mpv.
            case Key.PageUp when model.StepChapter(1):
                break;
            case Key.PageDown when model.StepChapter(-1):
                break;
            // A remote's or controller's next and previous: the channel, on a Channel; else 30 seconds.
            case Key.MediaNextTrack:
                if (!model.SwitchChannel(1)) model.SeekBy(30);
                break;
            case Key.MediaPreviousTrack:
                if (!model.SwitchChannel(-1)) model.SeekBy(-30);
                break;
            case Key.Escape when Window is { WindowState: WindowState.FullScreen }:
                ToggleFullScreen();
                break;
            case Key.Escape or Key.BrowserBack:
                model.CloseCommand.Execute(null);
                break;
            default:
                model.PokeControls();
                return;
        }
        e.Handled = true;
        model.PokeControls();
    }

    private void ToggleFullScreen()
    {
        if (Window is not { } window) return;
        if (window.WindowState == WindowState.FullScreen)
        {
            window.WindowState = stateBeforeFullScreen;
        }
        else
        {
            stateBeforeFullScreen = window.WindowState;
            window.WindowState = WindowState.FullScreen;
        }
        Focus();
    }
}
