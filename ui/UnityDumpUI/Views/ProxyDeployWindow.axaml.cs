// ProxyDeployWindow.axaml.cs — non-modal window code-behind. Owns no logic;
// the VM drives every command. Window lifetime: opened from MainWindow's
// "Proxy Deploy" button, closed by the user via the title-bar X.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class ProxyDeployWindow : Window
{
    public ProxyDeployWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ProxyDeployWindow(ProxyDeployViewModel vm) : this()
    {
        DataContext = vm;
    }
}
