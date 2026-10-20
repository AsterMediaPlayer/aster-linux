using System;
using System.Collections;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;

namespace Aster.Desktop.Controls;

/// <summary>
/// A titled horizontal row of cards, hidden when it has none. Arrows at its
/// ends page it sideways; a trackpad or Shift and the wheel scroll it too.
/// </summary>
public partial class ShelfView : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ShelfView, string?>(nameof(Title));

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<ShelfView, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<ShelfView, IDataTemplate?>(nameof(ItemTemplate));

    /// <summary>An item to bring into view, such as the episode after the one on the page.</summary>
    public static readonly StyledProperty<object?> ScrollTargetProperty =
        AvaloniaProperty.Register<ShelfView, object?>(nameof(ScrollTarget));

    public ShelfView()
    {
        InitializeComponent();
        PageLeft.Click += (_, _) => Page(-1);
        PageRight.Click += (_, _) => Page(1);
        Scroller.ScrollChanged += (_, _) => UpdateArrows();
        Scroller.SizeChanged += (_, _) => UpdateArrows();
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public object? ScrollTarget
    {
        get => GetValue(ScrollTargetProperty);
        set => SetValue(ScrollTargetProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            IsVisible = Items?.Cast<object>().Any() == true;
            Scroller.Offset = default;
            BringTargetIntoView();
        }
        if (change.Property == ScrollTargetProperty) BringTargetIntoView();
    }

    private void BringTargetIntoView()
    {
        if (ScrollTarget is not { } target) return;
        // Once the row has made its cards.
        Dispatcher.UIThread.Post(() =>
        {
            if (Row.ContainerFromItem(target) is not Control card) return;
            // As near the middle as the row can scroll.
            var middle = card.Bounds.X + Row.Margin.Left + card.Bounds.Width / 2 - Scroller.Viewport.Width / 2;
            Scroller.Offset = new Vector(Math.Clamp(middle, 0, Math.Max(0, Scroller.Extent.Width - Scroller.Viewport.Width)), 0);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Scrolls most of a row's width, leaving a card's width of the last page in view.</summary>
    private void Page(int direction)
    {
        var step = Math.Max(Scroller.Viewport.Width - 340, 300);
        var x = Math.Clamp(Scroller.Offset.X + direction * step, 0, Math.Max(0, Scroller.Extent.Width - Scroller.Viewport.Width));
        Scroller.Offset = new Vector(x, 0);
    }

    private void UpdateArrows()
    {
        PageLeft.Classes.Set("end", Scroller.Offset.X <= 1);
        PageRight.Classes.Set("end", Scroller.Offset.X >= Scroller.Extent.Width - Scroller.Viewport.Width - 1);
    }
}
