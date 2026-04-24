using Avalonia.Controls;
using Avalonia.Threading;

namespace AvaloniaTerminal.Samples;

public partial class ScrollSampleControl : UserControl
{
    private readonly TerminalControlModel _scrollSampleModel = new();
    private bool _scrollSampleQueued;

    public ScrollSampleControl()
    {
        InitializeComponent();
        ScrollTerminalControl.Model = _scrollSampleModel;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (_scrollSampleQueued || e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        _scrollSampleQueued = true;
        Dispatcher.UIThread.Post(() => TerminalSamples.LoadScrollSample(_scrollSampleModel));
    }
}
