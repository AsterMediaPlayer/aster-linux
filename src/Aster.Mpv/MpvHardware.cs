namespace Aster.Mpv;

/// <summary>Which hardware decoders mpv tries, for every player that shows video.</summary>
public static class MpvHardware
{
    /// <summary>
    /// mpv's <c>hwdec</c>. On Windows, whatever's safe (D3D11). On Linux,
    /// "auto-safe" tries NVIDIA's decoders (CUDA, VDPAU) and Vulkan's before
    /// VA-API, and their libraries print "Cannot load libcuda.so.1" and the
    /// like on any other machine; so it's VA-API alone (Intel, AMD, virtual
    /// GPUs), unless the NVIDIA driver is there. Where no decoder works, mpv
    /// decodes in software.
    ///
    /// VA-API's frames go straight into OpenGL only through EGL, and
    /// Avalonia draws on X11 with GLX, so there plain "vaapi" never starts
    /// and mpv went to software: a 4K HEVC film stuttered. "vaapi-copy"
    /// after it decodes on the GPU still and copies each frame back, which
    /// works under any OpenGL.
    /// </summary>
    public static string Decoding { get; } =
        !OperatingSystem.IsLinux() || Directory.Exists("/proc/driver/nvidia") ? "auto-safe" : "vaapi,vaapi-copy";
}
