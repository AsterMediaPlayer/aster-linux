using System;
using System.Linq;
using Aster.Desktop.Controls;
using Aster.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Aster.Desktop.Views;

/// <summary>
/// Every browse page: sizes the banner to the window, tells the page how
/// many cards fit across, asks for more as the grid nears the bottom, jumps
/// to a letter's section from the index, and moves the keyboard a row at a
/// time.
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
        List.AddHandler(KeyDownEvent, OnRowKeyDown, RoutingStrategies.Tunnel);
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
        var card = page.GridStyle switch
        {
            CardStyle.Landscape or CardStyle.Tile or CardStyle.Program or CardStyle.Timer or CardStyle.SeriesRule or CardStyle.Upcoming => 300,
            CardStyle.Channel => 200,
            _ => 180,
        };
        var room = Page.Bounds.Width - PagePadding - (page.IndexKeys.Count > 0 ? IndexRoom : 0);
        page.SetColumns((int)Math.Floor((room + CardSpacing) / (card + CardSpacing)));

    }

    // ---- Up and Down ----

    /// <summary>
    /// Up and Down go to the row above or below (past headings), onto what's
    /// nearest across from where the keyboard was, as the Apple TV moves. The
    /// rows are one virtualized list, so left to themselves the arrows pick by
    /// what happens to be laid out, which skips rows near the window's edge.
    /// </summary>
    private void OnRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || e.KeyModifiers != KeyModifiers.None || e.Source is not Visual source) return;
        if (RowContainer(source) is not { } container || source.TranslatePoint(new Point(source.Bounds.Width / 2, 0), List) is not { } from) return;
        var step = e.Key == Key.Down ? 1 : -1;
        if (MoveToRow(List.IndexFromContainer(container) + step, step, from.X)) e.Handled = true;
    }

    /// <summary>The first row from <paramref name="index"/>, going by <paramref name="step"/>, with something to focus: focused nearest <paramref name="x"/>. False when there's none.</summary>
    private bool MoveToRow(int index, int step, double x)
    {
        if (Model is not { } page) return false;
        for (; index >= 0 && index < page.Rows.Count; index += step)
        {
            if (page.Rows[index] is HeadingRow or StatusRow or PersonRow) continue;
            if (List.ContainerFromIndex(index) is not Control row)
            {
                // Not laid out yet: bring it in, then land on it.
                var target = index;
                List.ScrollIntoView(target);
                Dispatcher.UIThread.Post(() =>
                {
                    if (List.ContainerFromIndex(target) is Control laid && Nearest(laid, x) is { } control) KeyFocus.Move(control);
                }, DispatcherPriority.Loaded);
                return true;
            }
            if (Nearest(row, x) is { } nearest)
            {
                KeyFocus.Move(nearest);
                return true;
            }
        }
        return false;
    }

    /// <summary>What can take the keyboard in <paramref name="row"/>, nearest across to <paramref name="x"/>.</summary>
    private InputElement? Nearest(Control row, double x) =>
        row.GetVisualDescendants().OfType<InputElement>().Where(KeyFocus.CanFocus)
            .Select(element => (element, at: element.TranslatePoint(new Point(element.Bounds.Width / 2, 0), List)))
            .Where(c => c.at is not null)
            .OrderBy(c => Math.Abs(c.at!.Value.X - x))
            .Select(c => c.element)
            .FirstOrDefault();

    /// <summary>The row of the list <paramref name="from"/> is in.</summary>
    private Control? RowContainer(Visual from)
    {
        for (var node = from; node is not null; node = node.GetVisualParent())
        {
            if (node is Control control && List.IndexFromContainer(control) >= 0) return control;
        }
        return null;
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
