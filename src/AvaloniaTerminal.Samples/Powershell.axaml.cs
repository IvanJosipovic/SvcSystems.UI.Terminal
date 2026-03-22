using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AvaloniaTerminal.Samples;

public partial class Powershell : Window
{
    public Powershell()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
