using System.Windows;

namespace AgentDeck;

public partial class App : Application
{
    const string InstanceMutex = "AgentDeck.SingleInstance";
    const string ShowEvent = "AgentDeck.Show";

    Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--preview-keys")
        {
            Deck.KeyArt.WritePreview(e.Args[1]);
            Shutdown();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--render-deck") // docs screenshots
        {
            Deck.DeckMock.RenderAll(e.Args[1]);
            Shutdown();
            return;
        }
        _instance = new Mutex(true, InstanceMutex, out bool first);
        if (!first)
        {
            // Already running (probably in the tray): ask that copy to show itself.
            try { EventWaitHandle.OpenExisting(ShowEvent).Set(); } catch (Exception) { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("UI", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Info($"FATAL {args.ExceptionObject}");
        Env.ResetToLoginEnvironment();
        Log.Info($"AgentDeck starting ({Environment.ProcessPath}) args: {string.Join(' ', e.Args)}");

        var window = new MainWindow(startHidden: e.Args.Contains("--background"));

        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        new Thread(() =>
        {
            while (showSignal.WaitOne()) Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true, Name = "show-signal" }.Start();
    }
}
