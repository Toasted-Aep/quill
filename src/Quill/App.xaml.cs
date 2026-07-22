using Microsoft.UI.Xaml;

namespace Quill;

public partial class App : Application
{
    public static Window? MainWindowInstance { get; private set; }

    public App()
    {
        InitializeComponent();
        // Last-resort safety net: log recoverable UI exceptions instead of
        // tearing the app down (the log lives next to the notebooks).
        UnhandledException += (_, e) =>
        {
            try
            {
                var path = System.IO.Path.Combine(Services.LibraryStore.Dir, "crash.log");
                System.IO.File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
            }
            catch { }
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // TEMPORARY, Phase 1a substrate proof: redirect this PROCESS at a
        // throwaway library so the probe never touches the user's notes. Only
        // the in-memory setting changes; settings.json is not rewritten unless
        // the window closes gracefully. Remove with InkSurface.PaintProbe.cs.
        var probeData = Environment.GetEnvironmentVariable("QUILL_PAINT_PROBE_DATA");
        if (!string.IsNullOrEmpty(probeData)) Services.LibraryStore.Settings.DataFolder = probeData;

        // Deserialising a big library overlaps the window's XAML construction
        // instead of running after it (#roadmap: async library load).
        Services.LibraryStore.BeginLoad();
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }
}
