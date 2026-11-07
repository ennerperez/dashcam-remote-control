using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Dashcam.Remote.Player;

public partial class SplashWindow : Window
{
    private readonly DispatcherTimer _transitionTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };

    public SplashWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _transitionTimer.Tick += OnTransitionTimerTick;
        Opened += (_, _) => _transitionTimer.Start();
        Closed += (_, _) => _transitionTimer.Stop();
    }

    public event EventHandler? StartupReady;

    private void OnTransitionTimerTick(object? sender, EventArgs eventArgs)
    {
        _transitionTimer.Stop();
        StartupReady?.Invoke(this, EventArgs.Empty);
    }
}
