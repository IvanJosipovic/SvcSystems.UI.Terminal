using Avalonia.Controls;

namespace AvaloniaTerminal.Samples;

public partial class ScrollSampleControl : UserControl
{
    private readonly TerminalControlModel _scrollSampleModel = TerminalSamples.CreateScrollSampleModel();

    public ScrollSampleControl()
    {
        InitializeComponent();
        ScrollTerminalControl.Model = _scrollSampleModel;
    }
}
