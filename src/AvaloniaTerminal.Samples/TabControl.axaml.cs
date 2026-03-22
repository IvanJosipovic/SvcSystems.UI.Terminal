using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AvaloniaTerminal.Samples;

public partial class TabControl : Window
{
    public TabControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
