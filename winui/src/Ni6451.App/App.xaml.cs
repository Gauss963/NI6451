using Microsoft.UI.Xaml;

namespace Ni6451.App;

/// <summary>
/// Application entry point. Equivalent of the Python <c>main.py</c>: construct the main
/// window and show it. The Windows App SDK generates the actual <c>Main</c> (including the
/// unpackaged bootstrapper initialisation) because the project sets
/// <c>WindowsPackageType=None</c> together with <c>WindowsAppSDKSelfContained=true</c>.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
