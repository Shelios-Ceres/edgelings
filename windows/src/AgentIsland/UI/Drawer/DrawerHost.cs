using System.Threading;
using System.Windows.Threading;

namespace AgentIsland.UI.Drawer;

/// Runs the drawer on a UI thread of its own. The characters step the V8
/// engine and redraw every frame; on the app's thread that work (3-4 ms a
/// step) landed inside other windows' frames, and on a 240 Hz display the
/// Settings page dropped a frame every time. Calls from the app thread are
/// posted across; the drawer already marshals store notifications onto its
/// own dispatcher.
public sealed class DrawerHost
{
    private readonly Dispatcher _dispatcher;
    private DrawerWindow? _window;

    public DrawerHost(Action<Exception, string> logCrash)
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.UnhandledException += (_, args) => logCrash(args.Exception, "Drawer");
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Drawer UI",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
        Post(Open);
    }

    public void Reveal() => Post(() => _window?.Reveal());

    public void ToggleDeliberately() => Post(() => _window?.ToggleDeliberately());

    /// Recreates the window so a language change reaches its menus and card.
    public void Rebuild() => Post(() =>
    {
        _window?.Close();
        Open();
    });

    /// Closes the window (releasing the engine) and ends the thread.
    public void Shutdown()
    {
        if (_dispatcher.HasShutdownStarted) return;
        try
        {
            _dispatcher.Invoke(() => _window?.Close(), DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
        }
        _dispatcher.InvokeShutdown();
    }

    private void Open()
    {
        _window = new DrawerWindow();
        _window.Show();
    }

    private void Post(Action action) => _dispatcher.BeginInvoke(action);
}
