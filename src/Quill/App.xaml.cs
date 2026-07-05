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
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }
}
