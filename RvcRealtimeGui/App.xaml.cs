using Microsoft.UI.Xaml;

namespace RvcRealtimeGui;

public partial class App : Application
{
    Window? _window;

    public static Window MainWindowInstance { get; private set; } = null!;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        MainWindowInstance = _window;
        _window.Activate();
    }
}
