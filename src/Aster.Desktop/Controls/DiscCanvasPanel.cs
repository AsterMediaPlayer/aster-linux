using System;
using Aster.Core.DiscMenu;
using Avalonia;
using Avalonia.Controls;

namespace Aster.Desktop.Controls;

/// <summary>
/// Places each child at its <see cref="DiscPosition"/>: percent of the
/// panel, shifted by its anchor as a fraction of its own size. A width or
/// height in the position sizes the child; without, it takes its own. A
/// child with no position sits at the top left. The Apple app's
/// DiscCanvasLayout.
/// </summary>
public sealed class DiscCanvasPanel : Panel
{
    public static readonly AttachedProperty<DiscPosition?> PositionProperty =
        AvaloniaProperty.RegisterAttached<DiscCanvasPanel, Control, DiscPosition?>("Position");

    public static DiscPosition? GetPosition(Control control) => control.GetValue(PositionProperty);
    public static void SetPosition(Control control, DiscPosition? value) => control.SetValue(PositionProperty, value);

    static DiscCanvasPanel() => AffectsParentArrange<DiscCanvasPanel>(PositionProperty);

    private (double? Width, double? Height) SizeOf(DiscPosition position, Size canvas) =>
        (position.W is { } w ? canvas.Width * w / 100 : null, position.H is { } h ? canvas.Height * h / 100 : null);

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            if (GetPosition(child) is { } position)
            {
                var (width, height) = SizeOf(position, availableSize);
                child.Measure(new Size(width ?? double.PositiveInfinity, height ?? double.PositiveInfinity));
            }
            else
            {
                child.Measure(availableSize);
            }
        }
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            if (GetPosition(child) is not { } position)
            {
                child.Arrange(new Rect(child.DesiredSize));
                continue;
            }
            var (width, height) = SizeOf(position, finalSize);
            var finalWidth = width ?? child.DesiredSize.Width;
            var finalHeight = height ?? child.DesiredSize.Height;
            var shift = DiscMenuRules.AnchorShift(position.Anchor);
            var x = finalSize.Width * position.X / 100 + shift.X * finalWidth;
            var y = finalSize.Height * position.Y / 100 + shift.Y * finalHeight;
            child.Arrange(new Rect(x, y, Math.Max(0, finalWidth), Math.Max(0, finalHeight)));
        }
        return finalSize;
    }
}

/// <summary>Arranges its children in the largest 16:9 rectangle that fits, centred (letterboxed on anything that isn't 16:9).</summary>
public sealed class SixteenByNinePanel : Panel
{
    private static Rect Frame(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return default;
        var width = size.Width;
        var height = width * 9 / 16;
        if (height > size.Height)
        {
            height = size.Height;
            width = height * 16 / 9;
        }
        return new Rect((size.Width - width) / 2, (size.Height - height) / 2, width, height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var frame = Frame(availableSize);
        foreach (var child in Children) child.Measure(frame.Size);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var frame = Frame(finalSize);
        foreach (var child in Children) child.Arrange(frame);
        return finalSize;
    }
}

/// <summary>As wide as it's given, and 9/16 of that high.</summary>
public sealed class SixteenByNineBox : Decorator
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        var size = new Size(width, width * 9 / 16);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = new Size(finalSize.Width, finalSize.Width * 9 / 16);
        Child?.Arrange(new Rect(size));
        return size;
    }
}
