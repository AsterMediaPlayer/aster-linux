using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aster.Mpv;

/// <summary>
/// Draws a player's video with OpenGL into a framebuffer the UI owns, so
/// the app's controls can be drawn over it like any others. Every call,
/// creating and freeing included, must be made with that GL context current.
/// </summary>
public sealed unsafe class MpvRenderContext : IDisposable
{
    private IntPtr context;
    private readonly MpvPlayer player;
    private readonly Func<string, IntPtr> getProcAddress;
    private readonly Action frameReady;
    private GCHandle self;

    private MpvRenderContext(MpvPlayer player, Func<string, IntPtr> getProcAddress, Action frameReady)
    {
        this.player = player;
        this.getProcAddress = getProcAddress;
        this.frameReady = frameReady;
    }

    /// <param name="getProcAddress">The GL context's function lookup.</param>
    /// <param name="frameReady">
    /// Called on one of mpv's threads when there's a new frame to draw. It
    /// mustn't call into mpv; it should ask the UI for a redraw.
    /// </param>
    public static MpvRenderContext CreateOpenGl(MpvPlayer player, Func<string, IntPtr> getProcAddress, Action frameReady)
    {
        var render = new MpvRenderContext(player, getProcAddress, frameReady);
        render.self = GCHandle.Alloc(render);
        var apiType = Marshal.StringToCoTaskMemUTF8("opengl");
        try
        {
            var init = new MpvOpenGlInitParams
            {
                GetProcAddress = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr>)&GetProcAddress,
                GetProcAddressContext = GCHandle.ToIntPtr(render.self),
            };
            // On X11, the display lets mpv hand VA-API's frames to OpenGL
            // without copying them (given EGL); with none, it copies.
            var x11 = X11.Display;
            var parameters = stackalloc MpvRenderParam[]
            {
                new() { Type = MpvRenderParamType.ApiType, Data = apiType },
                new() { Type = MpvRenderParamType.OpenGlInitParams, Data = (IntPtr)(&init) },
                new() { Type = x11 == IntPtr.Zero ? MpvRenderParamType.Invalid : MpvRenderParamType.X11Display, Data = x11 },
                new() { Type = MpvRenderParamType.Invalid },
            };
            IntPtr created;
            MpvException.Check(MpvNative.mpv_render_context_create(&created, player.Handle, parameters), "Starting video output");
            render.context = created;
        }
        catch
        {
            render.self.Free();
            throw;
        }
        finally
        {
            Marshal.FreeCoTaskMem(apiType);
        }
        player.RenderContextCreated();
        MpvNative.mpv_render_context_set_update_callback(render.context, &OnUpdate, GCHandle.ToIntPtr(render.self));
        return render;
    }

    /// <summary>
    /// Draws the current frame into <paramref name="framebuffer"/>, scaled
    /// to <paramref name="width"/> × <paramref name="height"/> pixels.
    /// </summary>
    public void Render(int framebuffer, int width, int height, bool flipY)
    {
        if (context == IntPtr.Zero || width <= 0 || height <= 0) return;
        // Clears the "new frame" flag; drawing again without one is fine.
        MpvNative.mpv_render_context_update(context);
        var fbo = new MpvOpenGlFbo { Fbo = framebuffer, Width = width, Height = height };
        var flip = flipY ? 1 : 0;
        var parameters = stackalloc MpvRenderParam[]
        {
            new() { Type = MpvRenderParamType.OpenGlFbo, Data = (IntPtr)(&fbo) },
            new() { Type = MpvRenderParamType.FlipY, Data = (IntPtr)(&flip) },
            new() { Type = MpvRenderParamType.Invalid },
        };
        MpvNative.mpv_render_context_render(context, parameters);
    }

    public void Dispose()
    {
        if (context == IntPtr.Zero) return;
        MpvNative.mpv_render_context_free(context);
        context = IntPtr.Zero;
        self.Free();
        player.RenderContextFreed();
    }

    [UnmanagedCallersOnly]
    private static IntPtr GetProcAddress(IntPtr callbackContext, IntPtr name)
    {
        var render = (MpvRenderContext)GCHandle.FromIntPtr(callbackContext).Target!;
        return render.getProcAddress(Marshal.PtrToStringUTF8(name)!);
    }

    [UnmanagedCallersOnly]
    private static void OnUpdate(IntPtr callbackContext)
    {
        if (GCHandle.FromIntPtr(callbackContext).Target is MpvRenderContext render) render.frameReady();
    }
}
