using System.ComponentModel;
using Aster.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Aster.Desktop.Views;

public partial class DebugReportView : UserControl
{
    public DebugReportView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Ready for typing at a keyboard; from the couch, on Send, so a
    /// controller isn't caught in the text box. Once sent, on Done.
    /// </summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (Aster.Desktop.Platform.SteamSession.IsCouch) SendButton.Focus();
        else DescriptionBox.Focus();
        if (DataContext is DebugReportViewModel model)
        {
            model.PropertyChanged -= OnModelChanged;
            model.PropertyChanged += OnModelChanged;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DebugReportViewModel.Code) && sender is DebugReportViewModel { Code: not null })
            Dispatcher.UIThread.Post(() => DoneButton.Focus());
    }
}
