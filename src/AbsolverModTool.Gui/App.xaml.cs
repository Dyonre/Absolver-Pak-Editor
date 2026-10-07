using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using AbsolverModTool.Core;

namespace AbsolverModTool.Gui;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Nothing should ever crash silently - an unhandled exception on the UI thread, on a
        // background thread, or an unobserved Task fault all get their full detail (message,
        // stack trace, inner exceptions) written to the persistent log file before anything else
        // happens. For the UI-thread case specifically, mark it handled and keep the app running
        // (with a message box explaining what happened) rather than losing the user's in-progress
        // recipe to a hard crash over something that might be recoverable.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.WriteException("UNHANDLED (UI thread)", args.Exception);
            MessageBox.Show(
                $"An unexpected error occurred and was logged to:\n{AppLog.LogPath}\n\n{args.Exception.Message}\n\n" +
                "The app will try to keep running, but consider saving your recipe and restarting if anything looks wrong.",
                "Unexpected Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.WriteException("UNHANDLED (non-UI thread, fatal)", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "unknown"));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.WriteException("UNOBSERVED TASK EXCEPTION", args.Exception);
            args.SetObserved();
        };

        AppLog.Write($"=== AbsolverModTool.Gui starting (log: {AppLog.LogPath}) ===");
        base.OnStartup(e);
    }
}

