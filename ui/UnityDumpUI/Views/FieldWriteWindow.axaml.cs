// FieldWriteWindow.axaml.cs — Phase 21b code-behind.
//
// Modal dialog for writing a primitive Inspector field's storage in the
// host process. The VM owns the editor state + WriteCommand; the view's
// only job is to close the window when the VM fires CloseRequested (either
// after a successful write or after a Cancel click).

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using UnityDumpUI.ViewModels;

namespace UnityDumpUI.Views;

public partial class FieldWriteWindow : Window
{
    public FieldWriteWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DialogWindowGeometry.Attach(this);
    }

    public FieldWriteWindow(FieldWriteViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += (_, _) => Close();
    }
}
