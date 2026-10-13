using System;
using System.Threading.Tasks;
using System.Windows;
using CustomBrowser.Helpers;
using CustomBrowser.Views;

namespace CustomBrowser
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppLog.Info("=== MintBrowser started ===");

            // Crash handlers are registered first, so a failure while restoring downloads is logged too.
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                if (ex != null) AppLog.Error("Fatal unhandled exception", ex);
                ThemedDialog.Show($"The app hit a fatal error and has to close.\n\n{ex?.Message}\n\nDetails were saved to the log file:\n{AppLog.LogFilePath}", "App Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            DispatcherUnhandledException += (s, args) =>
            {
                AppLog.Error("Unhandled UI exception", args.Exception);
                ThemedDialog.Show($"Something went wrong:\n{args.Exception.Message}\n\nDetails were saved to the log file:\n{AppLog.LogFilePath}", "Unhandled Exception", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            // Fire-and-forget tasks (_ = SomethingAsync()) that throw end up here instead of vanishing.
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                AppLog.Error("Unobserved task exception", args.Exception);
                args.SetObserved();
            };

            try { CustomBrowser.Services.DownloadStore.Load(); } // restore the previous session's downloads before the first window opens
            catch (Exception loadEx) { AppLog.Error("Restoring downloads failed", loadEx); }

            base.OnStartup(e); // after the downloads are restored, so the first window sees them
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Save the downloads FIRST: once the shared database is closed below, saving to it silently does nothing.
            try { CustomBrowser.Services.DownloadStore.SaveNow(); }
            catch (Exception saveFirstEx) { AppLog.Error("Saving downloads before shutdown failed", saveFirstEx); }

            // Every window is closed at this point: safe to close the shared history/bookmarks database once.
            try
            {
                CustomBrowser.MainWindow.ShutdownServices(); // fully qualified: inside App, plain "MainWindow" means Application.MainWindow (the property)
            }
            catch (Exception ex)
            {
                AppLog.Error("Shutdown of shared services failed", ex);
            }
            AppLog.Info("=== MintBrowser exited ===");
            base.OnExit(e);
        }
    }
}

