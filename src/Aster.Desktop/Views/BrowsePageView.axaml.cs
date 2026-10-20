using System;
using Aster.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Aster.Desktop.Views;

/// <summary>
/// Every browse page: sizes the banner to the window, tells the page how
/// many cards fit across, asks for more as the grid nears the bottom, and
/// jumps to a letter's section from the index.
/// </summary>
public partial class BrowsePageView : UserControl
{
    private const double PagePadding = 48 * 2;
    private const double CardSpacing = 24;
    /// <summary>Room on the right for the letter index.</summary>
    private const double IndexRoom = 28;

    public BrowsePageView()
    {
        InitializeComponent();
        Page.SizeChanged += (_, e) =>
        {
            if (Model is { } page) page.HeroHeight = e.NewSize.Height;
            UpdateColumns();
        };
        Page.ScrollChanged += (_, _) => NearBottom();
        Index.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Content: string key }) JumpTo(key);
        });
        BackButton.Click += (_, _) => Navigator?.Back();
    }

    private BrowsePageViewModel? Model => DataContext as BrowsePageViewModel;

    private ShellViewModel? Navigator => this.FindAncestorOfType<ShellView>()?.DataContext as ShellViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Only pages opened over another have a way back; the sidebar's tabs don't.
        BackButton.IsVisible = Navigator?.CanGoBack == true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Model is { } page) page.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(BrowsePageViewModel.IndexKeys)) UpdateColumns();
        };
        UpdateColumns();
    }

    private void UpdateColumns()
    {
        if (Model is not { } page || Page.Bounds.Width <= 0) return;
        var card = page.LandscapeGrid ? 300 : 180;
        var room = Page.Bounds.Width - PagePadding - (page.IndexKeys.Count > 0 ? IndexRoom : 0);
        page.SetColumns((int)Math.Floor((room + CardSpacing) / (card + CardSpacing)));
    }

    private void NearBottom()
    {
        if (Model is { } page && Page.Offset.Y + Page.Viewport.Height > Page.Extent.Height - 1200) _ = page.LoadMoreAsync();
    }

    /// <summary>Brings a letter's heading to the top of the window.</summary>
    private void JumpTo(string key)
    {
        if (Model is not { } page) return;
        var row = page.RowOfSection(key);
        if (row < 0) return;
        List.ScrollIntoView(row);
        Dispatcher.UIThread.Post(() =>
        {
            if (List.ContainerFromIndex(row) is not Control heading) return;
            var top = heading.TranslatePoint(new Point(0, 0), List);
            if (top is { } point) Page.Offset = new Vector(0, Math.Max(0, point.Y));
        }, DispatcherPriority.Loaded);
    }
}
