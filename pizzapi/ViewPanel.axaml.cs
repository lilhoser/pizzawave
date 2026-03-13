using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace pizzapi;

public partial class ViewPanel : UserControl
{
    public ViewPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
