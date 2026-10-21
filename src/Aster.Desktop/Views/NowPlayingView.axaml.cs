using System;
using Aster.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Aster.Desktop.Views;

/// <summary>
/// Keeps the line being sung in the middle of the lyrics, drops the lyrics
/// or Up Next in a short window, and makes room for a book's chapters.
/// </summary>
public partial class NowPlayingView : UserControl
{
    public NowPlayingView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Lower.IsVisible = e.NewSize.Height >= 640;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not NowPlayingViewModel model) return;
        model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(NowPlayingViewModel.ShowChapters))
                BookLayout.ColumnDefinitions[1].Width = model.ShowChapters ? GridLength.Star : GridLength.Auto;
            if (change.PropertyName != nameof(NowPlayingViewModel.LyricIndex) || model.LyricIndex < 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (LyricLines.ContainerFromIndex(model.LyricIndex) is not Control line) return;
                var middle = line.Bounds.Y + LyricLines.Margin.Top + line.Bounds.Height / 2 - LyricsScroller.Viewport.Height / 2;
                LyricsScroller.Offset = new Avalonia.Vector(0, Math.Max(0, middle));
            }, DispatcherPriority.Loaded);
        };
    }
}
