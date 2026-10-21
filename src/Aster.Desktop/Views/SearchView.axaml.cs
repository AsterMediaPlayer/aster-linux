using Avalonia;
using Avalonia.Controls;

namespace Aster.Desktop.Views;

/// <summary>The search box takes the keys whenever the tab is shown.</summary>
public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FocusBox();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is Aster.Desktop.ViewModels.SearchViewModel model) model.FocusRequested += FocusBox;
    }

    private void FocusBox() => Avalonia.Threading.Dispatcher.UIThread.Post(() => Box.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
}
