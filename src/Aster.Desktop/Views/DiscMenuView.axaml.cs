using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Aster.Core.DiscMenu;
using Aster.Desktop.Controls;
using Aster.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Aster.Desktop.Views;

/// <summary>
/// Draws a disc menu over the whole window: background, layers, title and
/// buttons on a 16:9 canvas, each screen arriving with its transition (CLIENT_GUIDE
/// §5.4: fade, slide, rise, zoom or wipe, the leaving screen easing out as
/// the new one eases in). Driven as the Apple TV's is: the arrows (or a
/// remote's, or a controller's) move between buttons, Enter chooses, Esc or
/// Back steps back a page, then a screen, then closes, and Space or the play
/// key plays what the button plays. The mouse points and clicks.
/// </summary>
public partial class DiscMenuView : UserControl
{
    /// <summary>The screen drawn, with its buttons in drawing order and which navigator generation it is.</summary>
    private sealed record DrawnScreen(Panel Layer, IReadOnlyList<DiscButtonView> Buttons, int Generation);

    private DrawnScreen? current;
    private DiscMenuViewModel? model;
    private DiscBackground? drawnBackground;
    private bool hasBackground;
    private IReadOnlyList<DiscLayer>? drawnLayers;
    private Control? still;
    private Control? poster;
    private Window? window;

    public DiscMenuView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as DiscMenuViewModel);
        Canvas.PointerMoved += (_, e) =>
        {
            var index = ButtonAt(e);
            Canvas.Cursor = index >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            if (index >= 0) model?.Navigator.Hover(index);
        };
        Canvas.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(Canvas).Properties;
            if (point.IsXButton1Pressed)
            {
                model?.Navigator.HardwareBack();
                e.Handled = true;
                return;
            }
            if (!point.IsLeftButtonPressed || current is null || model?.AlertMessage is not null) return;
            var at = e.GetPosition(current.Layer);
            model?.Navigator.Tap(at.X, at.Y);
            e.Handled = true;
        };
    }

    private void Attach(DiscMenuViewModel? next)
    {
        if (ReferenceEquals(next, model)) return;
        if (model is not null)
        {
            model.ScreenChanged -= Draw;
            model.FocusChanged -= ShowFocus;
            model.PropertyChanged -= ModelChanged;
        }
        model = next;
        if (model is null) return;
        model.ScreenChanged += Draw;
        model.FocusChanged += ShowFocus;
        model.PropertyChanged += ModelChanged;
        Draw();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DiscMenuViewModel.IsTrailerShowing) || model is null) return;
        if (poster is not null) poster.Opacity = model.IsTrailerShowing ? 0 : 1;
    }

    // ---- Drawing ----

    private void Draw()
    {
        if (model is null) return;
        var navigator = model.Navigator;
        var screen = navigator.Screen;
        var duration = screen.TransitionDuration;
        var animate = navigator.AnimateChange && screen.TransitionStyle != "none" && duration > TimeSpan.Zero;

        // The background crossfades: the old still fades from over the new.
        if (!hasBackground || drawnBackground != screen.Background)
        {
            hasBackground = true;
            drawnBackground = screen.Background;
            var leaving = still;
            still = DiscMenuBackdrops.Still(screen.Background, model.Resolver, out poster);
            if (poster is not null) poster.Opacity = model.IsTrailerShowing ? 0 : 1;
            StillHost.Children.Insert(0, still);
            if (leaving is not null) Remove(StillHost, leaving, animate ? duration : TimeSpan.Zero, t => leaving.Opacity = 1 - EaseIn(t));
        }

        // Layers are replaced, the new ones fading in.
        var layers = screen.Layout.Layers ?? [];
        if (drawnLayers is null || !drawnLayers.SequenceEqual(layers))
        {
            drawnLayers = layers;
            LayersHost.Children.Clear();
            var drawn = DiscMenuBackdrops.Layers(layers, model.Resolver);
            LayersHost.Children.Add(drawn);
            if (animate) Animate(duration, t => drawn.Opacity = EaseOut(t));
        }

        // The screen itself, with its transition.
        var leavingScreen = current;
        current = DrawScreen(screen, navigator.Generation);
        ScreenHost.Children.Add(current.Layer);
        current.Layer.LayoutUpdated += ReportFrames;
        ShowFocus();
        if (leavingScreen is not null)
        {
            leavingScreen.Layer.LayoutUpdated -= ReportFrames;
            leavingScreen.Layer.IsHitTestVisible = false;
        }
        if (animate)
        {
            var arriving = Transition(current.Layer, screen.TransitionStyle, navigator.Direction, entering: true);
            Animate(duration, arriving);
            if (leavingScreen is not null) Remove(ScreenHost, leavingScreen.Layer, duration, Transition(leavingScreen.Layer, screen.TransitionStyle, navigator.Direction, entering: false));
        }
        else if (leavingScreen is not null)
        {
            ScreenHost.Children.Remove(leavingScreen.Layer);
        }
    }

    /// <summary>A screen's title and buttons, placed where the menu says or in the default column.</summary>
    private DrawnScreen DrawScreen(DiscResolvedScreen screen, int generation)
    {
        var style = new DiscMenuStyle(screen.Theme);
        var layer = new Panel { Width = DiscMenuStyle.Width, Height = DiscMenuStyle.Height };
        var buttons = screen.Entries.Select(e => new DiscButtonView(e, style, screen.Layout.ButtonStyle, model!.Resolver)).ToList();
        var showTitle = screen.Layout.HideTitle != true;
        TextBlock Title() => style.Text(screen.Title, style.TitleFont);

        if (screen.Positioned)
        {
            var canvas = new DiscCanvasPanel();
            if (showTitle)
            {
                var title = Title();
                DiscCanvasPanel.SetPosition(title, screen.Layout.TitlePosition ?? new DiscPosition(0, 0, 100));
                canvas.Children.Add(title);
            }
            foreach (var button in buttons)
            {
                DiscCanvasPanel.SetPosition(button, button.Entry.Position);
                canvas.Children.Add(button);
            }
            layer.Children.Add(canvas);
        }
        else
        {
            var column = new StackPanel
            {
                Margin = new Thickness(style.BaseEm * 4),
                HorizontalAlignment = style.Alignment,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (showTitle && screen.Layout.TitlePosition is null)
            {
                var title = Title();
                title.HorizontalAlignment = style.Alignment;
                title.Margin = new Thickness(0, 0, 0, style.TitleFont * 0.75);
                column.Children.Add(title);
            }
            var list = new StackPanel { Spacing = style.BaseEm * 0.5, HorizontalAlignment = style.Alignment };
            foreach (var button in buttons) list.Children.Add(button);
            column.Children.Add(list);
            layer.Children.Add(column);
            if (showTitle && screen.Layout.TitlePosition is { } position)
            {
                var canvas = new DiscCanvasPanel();
                var title = Title();
                DiscCanvasPanel.SetPosition(title, position);
                canvas.Children.Add(title);
                layer.Children.Add(canvas);
            }
        }
        return new DrawnScreen(layer, buttons, generation);
    }

    private void ShowFocus()
    {
        if (current is null || model is null) return;
        for (var i = 0; i < current.Buttons.Count; i++) current.Buttons[i].SetFocused(i == model.Navigator.FocusIndex);
    }

    /// <summary>The buttons' frames, as laid out on the screen, for moving between them by direction and for clicks.</summary>
    private void ReportFrames(object? sender, EventArgs e)
    {
        if (current is null || model is null) return;
        var rects = new List<DiscRect>(current.Buttons.Count);
        foreach (var button in current.Buttons)
        {
            if (button.TranslatePoint(default, current.Layer) is not { } origin) return;
            rects.Add(new DiscRect(origin.X, origin.Y, button.Bounds.Width, button.Bounds.Height));
        }
        model.Navigator.ReportFrames(rects, current.Generation);
    }

    private int ButtonAt(PointerEventArgs e)
    {
        if (current is null || model?.AlertMessage is not null) return -1;
        var at = e.GetPosition(current.Layer);
        for (var i = current.Buttons.Count - 1; i >= 0; i--)
        {
            var button = current.Buttons[i];
            if (button.TranslatePoint(default, current.Layer) is { } origin && new Rect(origin, button.Bounds.Size).Contains(at)) return i;
        }
        return -1;
    }

    // ---- Transitions ----

    private static double EaseOut(double t) => 1 - (1 - t) * (1 - t);
    private static double EaseIn(double t) => t * t;

    /// <summary>
    /// How a screen moves as it arrives (<paramref name="entering"/>) or
    /// leaves, by the transition's style; <paramref name="direction"/> is +1
    /// going forward and -1 going back.
    /// </summary>
    private static Action<double> Transition(Panel layer, string style, int direction, bool entering)
    {
        var sign = direction >= 0 ? 1 : -1;
        var translate = new TranslateTransform();
        var scale = new ScaleTransform();
        layer.RenderTransform = new TransformGroup { Children = { scale, translate } };
        Action<double> fade = entering ? t => layer.Opacity = EaseOut(t) : t => layer.Opacity = 1 - EaseIn(t);
        switch (style)
        {
            case "fade":
                return fade;
            case "slide":
            {
                var shift = DiscMenuStyle.Width * 0.08 * sign;
                return t =>
                {
                    fade(t);
                    translate.X = entering ? shift * (1 - EaseOut(t)) : -shift * EaseIn(t);
                };
            }
            case "rise":
            {
                var shift = DiscMenuStyle.Height * 0.06 * sign;
                return t =>
                {
                    fade(t);
                    translate.Y = entering ? shift * (1 - EaseOut(t)) : -shift * EaseIn(t);
                };
            }
            case "zoom":
            {
                var forward = direction >= 0;
                var from = entering ? (forward ? 0.92 : 1.08) : 1;
                var to = entering ? 1 : (forward ? 1.08 : 0.92);
                return t =>
                {
                    fade(t);
                    var eased = entering ? EaseOut(t) : EaseIn(t);
                    scale.ScaleX = scale.ScaleY = from + (to - from) * eased;
                };
            }
            case "wipe":
                if (!entering) return fade;
                var fromLeading = direction >= 0;
                return t =>
                {
                    var width = DiscMenuStyle.Width * Math.Clamp(EaseOut(t), 0, 1);
                    layer.Clip = t >= 1 ? null : new RectangleGeometry(new Rect(fromLeading ? 0 : DiscMenuStyle.Width - width, 0, width, DiscMenuStyle.Height));
                };
            default:
                return _ => { };
        }
    }

    /// <summary>Steps <paramref name="step"/> from 0 to 1 over <paramref name="duration"/>, a frame at a time.</summary>
    private void Animate(TimeSpan duration, Action<double> step, Action? done = null)
    {
        step(0);
        if (TopLevel.GetTopLevel(this) is not { } top || duration <= TimeSpan.Zero)
        {
            step(1);
            done?.Invoke();
            return;
        }
        TimeSpan? start = null;
        void Frame(TimeSpan now)
        {
            start ??= now;
            var t = Math.Clamp((now - start.Value) / duration, 0, 1);
            step(t);
            if (t < 1) top.RequestAnimationFrame(Frame);
            else done?.Invoke();
        }
        top.RequestAnimationFrame(Frame);
    }

    /// <summary>Takes <paramref name="child"/> out of <paramref name="host"/> once its leaving animation has played.</summary>
    private void Remove(Panel host, Control child, TimeSpan duration, Action<double> leaving)
    {
        child.IsHitTestVisible = false;
        Animate(duration, leaving, () => host.Children.Remove(child));
    }

    // ---- Keys and the window ----

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        window = TopLevel.GetTopLevel(this) as Window;
        window?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        // Like putting in a disc: full screen when videos play full screen, and through to the player.
        FullScreen.Hold(window, enter: Aster.Core.AppSettings.Current.FullScreenPlayback);
        Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        window?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        FullScreen.Release(window);
        window = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The menu's keys, heard at the window before anything else, wherever the keyboard focus was left.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (model is not { } m) return;
        var navigator = m.Navigator;
        if (m.AlertMessage is not null)
        {
            if (e.Key is Key.Enter or Key.Space or Key.Escape or Key.BrowserBack or Key.Back) m.AlertMessage = null;
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Left: navigator.Move(-1, 0); break;
            case Key.Right: navigator.Move(1, 0); break;
            case Key.Up: navigator.Move(0, -1); break;
            case Key.Down: navigator.Move(0, 1); break;
            case Key.Enter: navigator.Select(); break;
            case Key.Space or Key.MediaPlayPause: navigator.PlayPause(); break;
            case Key.Escape or Key.BrowserBack or Key.Back: navigator.HardwareBack(); break;
            case Key.F or Key.F11 when window is not null: FullScreen.Toggle(window); break;
            default: return;
        }
        e.Handled = true;
    }
}
