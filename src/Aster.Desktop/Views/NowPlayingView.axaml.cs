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

    /// <summary>A click beside Play On's card closes it; one on the card itself doesn't.</summary>
    private void OnSheetBackdropPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is NowPlayingViewModel model) model.CloseSpeakers();
    }

    private void OnSheetPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) => e.Handled = true;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not NowPlayingViewModel model) return;
        model.PropertyChanged += (_, change) =>
        {
            // Play On opened: the controller on its first row once they're found; closed, back on the button that opened it.
            if (change.PropertyName is nameof(NowPlayingViewModel.SpeakerChoices) or nameof(NowPlayingViewModel.IsChoosingSpeakers))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (model.IsChoosingSpeakers)
                    {
                        var focused = Aster.Desktop.Controls.KeyFocus.Current(this) as Visual;
                        if ((focused is null || !Aster.Desktop.Controls.KeyFocus.IsWithin(focused, SpeakerSheet)) && Aster.Desktop.Controls.KeyFocus.FirstIn(SpeakerSheet) is { } first)
                            Aster.Desktop.Controls.KeyFocus.Move(first);
                    }
                    else if (change.PropertyName == nameof(NowPlayingViewModel.IsChoosingSpeakers)) Aster.Desktop.Controls.KeyFocus.Move(SpeakersButton);
                }, DispatcherPriority.Loaded);
            }
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
