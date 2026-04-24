using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SvcSystems.UI.Terminal.Samples;

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
