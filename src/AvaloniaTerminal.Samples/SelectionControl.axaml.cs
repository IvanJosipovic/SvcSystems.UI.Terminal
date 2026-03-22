using Avalonia.Controls;

namespace AvaloniaTerminal.Samples;

public partial class SelectionControl : UserControl
{
    private readonly TerminalControlModel _selectionModel = TerminalSamples.CreateSelectionSampleModel();

    public SelectionControl()
    {
        InitializeComponent();
        DataContext = _selectionModel;
        SelectionTerminalControl.Model = _selectionModel;
    }
}
