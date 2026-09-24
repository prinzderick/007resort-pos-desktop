using System.Windows;
using System.Windows.Controls;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.App.Views;

/// <summary>Supervisor PIN entry: a <see cref="PasswordBox"/> cannot be data-bound, so it is synced here and cleared as soon as the view model clears it.</summary>
public partial class SensitiveActionView : UserControl
{
    public SensitiveActionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is SensitiveActionViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SensitiveActionViewModel.SupervisorPin) && string.IsNullOrEmpty(vm.SupervisorPin))
                {
                    SupervisorPinBox.Clear();
                }
            };
        }
    }

    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SensitiveActionViewModel vm)
        {
            vm.SupervisorPin = SupervisorPinBox.Password;
        }
    }
}
