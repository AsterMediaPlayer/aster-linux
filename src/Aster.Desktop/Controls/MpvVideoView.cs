using System;
using Aster.Mpv;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;

namespace Aster.Desktop.Controls;

/// <summary>
/// Shows a player's video. mpv draws each frame with OpenGL into this
/// control's framebuffer, so other controls can sit on top of it. With no
/// player, or before its first frame, it's black.
/// </summary>
public sealed class MpvVideoView : OpenGlControlBase
{
    public static readonly StyledProperty<MpvPlayer?> PlayerProperty =
        AvaloniaProperty.Register<MpvVideoView, MpvPlayer?>(nameof(Player));

    public MpvPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    private MpvRenderContext? render;
    private MpvPlayer? renderedPlayer;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // The render context is made on the next frame, where GL is current.
        if (change.Property == PlayerProperty) RequestNextFrameRendering();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!ReferenceEquals(renderedPlayer, Player))
        {
            ReleaseRender();
            renderedPlayer = Player;
            if (Player is { } player)
            {
                try { render = MpvRenderContext.CreateOpenGl(player, gl.GetProcAddress, FrameReady); }
                catch (MpvException) { render = null; }
                catch (ObjectDisposedException) { render = null; }
            }
        }

        if (render is null)
        {
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);
            return;
        }
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        var height = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));
        render.Render(fb, width, height, flipY: true);
    }

    /// <summary>Which OpenGL the video draws with, in the log: "Mesa SVGA3D", or software llvmpipe.</summary>
    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        System.Diagnostics.Debug.WriteLine($"Video: OpenGL {gl.GetString(GlConsts.GL_VERSION)} on {gl.GetString(GlConsts.GL_RENDERER)} ({gl.GetString(GlConsts.GL_VENDOR)})");
    }

    protected override void OnOpenGlDeinit(GlInterface gl) => ReleaseRender();

    private void ReleaseRender()
    {
        render?.Dispose();
        render = null;
        renderedPlayer = null;
    }

    // From one of mpv's threads.
    private void FrameReady() => Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
}
