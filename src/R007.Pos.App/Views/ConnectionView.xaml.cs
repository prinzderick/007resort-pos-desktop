using System.Windows;
using System.Windows.Controls;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.App.Views;

public partial class ConnectionView : UserControl
{
    public ConnectionView() => InitializeComponent();

    // PasswordBox cannot be data-bound: hand the typed PIN to the view-model.
    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionViewModel vm)
        {
            vm.EnteredPin = PinBox.Password;
        }
    }
}
