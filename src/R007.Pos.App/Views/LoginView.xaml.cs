using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.App.Views;

/// <summary>The password box cannot be data-bound: its text is synced to the view model and cleared once the view model clears it.</summary>
public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is LoginViewModel vm)
        {
            vm.PropertyChanged += (_, args) => OnViewModelChanged(vm, args);
        }
    }

    private void OnViewModelChanged(LoginViewModel vm, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LoginViewModel.Password) && string.IsNullOrEmpty(vm.Password))
        {
            PasswordBox.Clear();
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
        {
            vm.Password = PasswordBox.Password;
        }
    }
}
