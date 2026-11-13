using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Aster.Desktop.Controls;

/// <summary>
/// A profile's circle: the initial on its colour, with the picture over it
/// once it arrives (and only the initial if there's none, or it doesn't
/// load). The Apple app's ProfileAvatarView.
/// </summary>
public sealed class ProfileAvatar : Panel
{
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<ProfileAvatar, double>(nameof(Size), 88);
    public static readonly StyledProperty<string?> InitialProperty = AvaloniaProperty.Register<ProfileAvatar, string?>(nameof(Initial));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<ProfileAvatar, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<Uri?> ImageUrlProperty = AvaloniaProperty.Register<ProfileAvatar, Uri?>(nameof(ImageUrl));

    private readonly Ellipse circle = new();
    private readonly TextBlock letter = new() { Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly RemoteImage picture = new() { Stretch = Stretch.UniformToFill };
    private readonly Border clip = new() { ClipToBounds = true };

    public ProfileAvatar()
    {
        clip.Child = picture;
        Children.Add(circle);
        Children.Add(letter);
        Children.Add(clip);
        Apply();
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public string? Initial
    {
        get => GetValue(InitialProperty);
        set => SetValue(InitialProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Uri? ImageUrl
    {
        get => GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SizeProperty || change.Property == InitialProperty || change.Property == FillProperty || change.Property == ImageUrlProperty) Apply();
    }

    private void Apply()
    {
        Width = Height = Size;
        circle.Fill = Fill ?? new SolidColorBrush(Color.FromRgb(0x4D, 0x4D, 0x4D));
        letter.Text = Initial;
        letter.FontSize = Size * 0.42;
        clip.CornerRadius = new CornerRadius(Size / 2);
        picture.Url = ImageUrl;
        clip.IsVisible = ImageUrl is not null;
    }
}
