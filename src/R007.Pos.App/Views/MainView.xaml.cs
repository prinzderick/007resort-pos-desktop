using System.Windows.Controls;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.App.Views;

public partial class MainView : UserControl
{
    public MainView() => InitializeComponent();

    private void OnTabSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel main && Tabs.SelectedItem is NavItem item && !ReferenceEquals(item, main.Selected))
        {
            main.SelectCommand.Execute(item);
        }
    }
}
