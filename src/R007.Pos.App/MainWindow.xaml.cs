using System.Net.Http;
using System.Windows;
using R007.Pos.Core.Api;

namespace R007.Pos.App;

/// <summary>
/// Placeholder shell. Real screens are selected from the device registration, facility capabilities
/// and staff permissions returned by the API (see <c>TerminalContext</c>).
/// </summary>
public partial class MainWindow : Window
{
    private readonly IR007ApiClient _apiClient;

    public MainWindow(IR007ApiClient apiClient)
    {
        _apiClient = apiClient;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var info = await _apiClient.GetSystemInfoAsync();
            ApiText.Text = $"Connected to {info.Service} {info.Version} ({info.Mode}, {info.Environment})";
        }
        catch (HttpRequestException)
        {
            ApiText.Text = "Site API unreachable";
        }
        catch (TaskCanceledException)
        {
            ApiText.Text = "Site API timed out";
        }
    }
}
