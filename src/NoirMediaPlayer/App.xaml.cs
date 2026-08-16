using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace NoirMediaPlayer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The player drives most of its work from `async void` event handlers, so an
        // unobserved exception would otherwise terminate the process without a diagnostic.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Debug.WriteLine(e.Exception);
        e.Handled = true;

        const string caption = "NOIR";
        var message = $"NOIR hit an unexpected error and recovered.\n\n{e.Exception.Message}";
        try
        {
            if (MainWindow is { IsLoaded: true } owner)
            {
                MessageBox.Show(owner, message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            // Never let the reporting path itself take the process down.
            Debug.WriteLine(exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Debug.WriteLine(e.Exception);
        e.SetObserved();
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        Debug.WriteLine(e.ExceptionObject);
}
