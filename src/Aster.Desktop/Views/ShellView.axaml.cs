using System;
using Aster.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;

namespace Aster.Desktop.Views;

/// <summary>
/// The collapsing sidebar, as on the Apple TV: the pointer at the window's
/// left edge or resting on the capsule slides it out, as does the keyboard
/// arriving in it (Tab); the pointer leaving it, a click on the page, the
/// keyboard leaving it, or Esc puts it away.
/// </summary>
public partial class ShellView : UserControl
{
    /// <summary>A moment's grace, so a pointer passing over the capsule, or wavering at the sidebar's edge, doesn't flick it in and out.</summary>
    private readonly DispatcherTimer hoverOpen = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer leaveClose = new() { Interval = TimeSpan.FromMilliseconds(350) };

    public ShellView()
    {
        InitializeComponent();
        hoverOpen.Tick += (_, _) => { hoverOpen.Stop(); Open(); };
        leaveClose.Tick += (_, _) =>
        {
            leaveClose.Stop();
            if (!Sidebar.IsPointerOver && !HasFocusWithin(Sidebar)) Close();
        };

        Edge.PointerEntered += (_, _) => Open();
        Capsule.PointerEntered += (_, _) => hoverOpen.Start();
        Capsule.PointerExited += (_, _) => hoverOpen.Stop();
        Sidebar.PointerEntered += (_, _) => leaveClose.Stop();
        Sidebar.PointerExited += (_, _) => leaveClose.Start();
        Scrim.PointerPressed += (_, e) =>
        {
            Close();
            e.Handled = true;
        };

        // Sign Out asks in a popup, which takes the keyboard: the sidebar stays while it's open.
        if (SignOut.Flyout is { } question)
        {
            question.Opened += (_, _) => popupOpen = true;
            question.Closed += (_, _) =>
            {
                popupOpen = false;
                leaveClose.Start();
            };
        }

        // The keyboard: tabbing into the sidebar slides it out; tabbing on out of it puts it away.
        Sidebar.GotFocus += (_, _) => Open();
        Sidebar.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!HasFocusWithin(Sidebar) && !Sidebar.IsPointerOver) Close();
        }, DispatcherPriority.Input);
    }

    private ShellViewModel? Model => DataContext as ShellViewModel;

    private void Open()
    {
        if (Model is { } shell) shell.IsSidebarOpen = true;
    }

    private void Close()
    {
        if (Model is { } shell) shell.IsSidebarOpen = false;
    }

    /// <summary>The keyboard is on something in <paramref name="container"/>, or in a popup it opened (Sign Out's question).</summary>
    private bool HasFocusWithin(Control container)
    {
        if (popupOpen) return true;
        for (var node = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Avalonia.Visual; node is not null; node = node.GetVisualParent())
        {
            if (node == container) return true;
        }
        return false;
    }

    private bool popupOpen;
}
