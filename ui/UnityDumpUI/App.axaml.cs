using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UnityDumpUI.Views;

namespace UnityDumpUI;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            // Restore last-session position / size / maximized state BEFORE the
            // window is shown (so saved geometry applies without a visible jump),
            // validated against the monitors present this session on Opened.
            mainWindow.AttachWindowState(new Services.WindowStateStore());
            desktop.MainWindow = mainWindow;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
