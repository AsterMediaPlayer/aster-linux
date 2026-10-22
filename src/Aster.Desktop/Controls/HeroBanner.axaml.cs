using System;
using Aster.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Aster.Desktop.Controls;

/// <summary>
/// A featured banner; the arrow keys page it while it has the keyboard, and
/// the keyboard moves onto the new page's Play. Left on the first page goes
/// on to the sidebar.
/// </summary>
public partial class HeroBanner : UserControl
{
    public HeroBanner()
    {
        InitializeComponent();
        // First, before the pages inside can take the arrows.
        AddHandler(KeyDownEvent, OnKeyDownFirst, RoutingStrategies.Tunnel);
    }

    private void OnKeyDownFirst(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Handled || DataContext is not HeroModel hero) return;
        if (e.Key == Key.Right && hero.HasNext) hero.NextCommand.Execute(null);
        else if (e.Key == Key.Left && hero.HasPrevious) hero.PreviousCommand.Execute(null);
        else return;
        e.Handled = true;
        FocusPage(hero.Index);
    }

    /// <summary>The new page's Play, once the carousel has made it, and again once it has slid in (the slide can drop the keyboard).</summary>
    private void FocusPage(int index)
    {
        void Focus()
        {
            if (DataContext is HeroModel { Index: var showing } && showing == index
                && Pages.ContainerFromIndex(index) is { } page && KeyFocus.FirstIn(page) is { } play && !play.IsFocused)
            {
                play.Focus(NavigationMethod.Directional);
            }
        }
        Dispatcher.UIThread.Post(Focus, DispatcherPriority.Loaded);
        DispatcherTimer.RunOnce(Focus, TimeSpan.FromMilliseconds(450));
    }
}

/// <summary>
/// The banner's pages, which leave the arrow keys alone: the banner pages
/// itself, and on its first or last page the keys go on (to the sidebar,
/// say) rather than stopping here.
/// </summary>
public sealed class HeroCarousel : Carousel
{
    protected override Type StyleKeyOverride => typeof(Carousel);

    protected override void OnKeyDown(KeyEventArgs e) { }
}
