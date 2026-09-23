using System.Windows;
using System.Windows.Controls;

namespace R007.Pos.App.Views;

public partial class SellView : UserControl
{
    public SellView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Keep the scan box ready: a keyboard-wedge scanner types wherever focus is.
        if (ScanBox.IsVisible)
        {
            ScanBox.Focus();
        }
    }
}
