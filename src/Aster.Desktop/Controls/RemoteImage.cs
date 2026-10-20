using System;
using Aster.Desktop.Images;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Aster.Desktop.Controls;

/// <summary>
/// An Image whose picture comes from the server by address. It fades in once
/// loaded; until then (or if it can't load) whatever is behind it shows,
/// usually a placeholder in the card. <see cref="HasImage"/> says whether a
/// picture is showing, for a fallback such as a title in place of a logo.
/// </summary>
public class RemoteImage : Image
{
    public static readonly StyledProperty<Uri?> UrlProperty =
        AvaloniaProperty.Register<RemoteImage, Uri?>(nameof(Url));

    public static readonly DirectProperty<RemoteImage, bool> HasImageProperty =
        AvaloniaProperty.RegisterDirect<RemoteImage, bool>(nameof(HasImage), o => o.HasImage);

    private bool hasImage;

    static RemoteImage()
    {
        StretchProperty.OverrideDefaultValue<RemoteImage>(Avalonia.Media.Stretch.UniformToFill);
    }

    public RemoteImage()
    {
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) }];
    }

    protected override Type StyleKeyOverride => typeof(Image);

    public Uri? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public bool HasImage
    {
        get => hasImage;
        private set => SetAndRaise(HasImageProperty, ref hasImage, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty) _ = LoadAsync(Url);
    }

    private async System.Threading.Tasks.Task LoadAsync(Uri? url)
    {
        if (url is null)
        {
            Show(null);
            return;
        }
        // From memory at once, so a card scrolled back to doesn't blink.
        if (ImageLoader.Cached(url) is { } cached)
        {
            Show(cached, fade: false);
            return;
        }
        Show(null);
        var bitmap = await ImageLoader.LoadAsync(url);
        // Another address may have been set while this one loaded.
        if (Url == url) Show(bitmap);
    }

    private void Show(Bitmap? bitmap, bool fade = true)
    {
        Source = bitmap;
        HasImage = bitmap is not null;
        if (!fade)
        {
            var transitions = Transitions;
            Transitions = null;
            Opacity = bitmap is null ? 0 : 1;
            Transitions = transitions;
        }
        else
        {
            Opacity = bitmap is null ? 0 : 1;
        }
    }
}
