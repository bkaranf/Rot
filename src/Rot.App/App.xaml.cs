using System.Windows;
using Rot.App.Interop;
using Rot.App.Services;
using Rot.App.Updates;

namespace Rot.App;

public partial class App : Application
{
    private ApplicationController? _controller;
    private SingleInstanceGuard? _singleInstance;
    private InstanceIpcServer? _instanceServer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = SingleInstanceGuard.TryAcquire();
        if (_singleInstance is null)
        {
            if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var response = await SingleInstanceGuard.ForwardToRunningInstanceAsync(
                        InstanceRequest.OpenSettings(), TimeSpan.FromSeconds(10));
                    if (!response.Ok)
                    {
                        MessageBox.Show(response.Message, "Rot", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception exception)
                {
                    MessageBox.Show("Rot is running, but Settings could not open. " + exception.Message,
                        "Rot", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Console.Error.WriteLine($"[rot] ERROR Unhandled UI exception: {args.Exception}");
            MessageBox.Show(
                args.Exception.Message,
                "Rot encountered an error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            _controller = ApplicationController.CreateDefault();
            await _controller.StartAsync(e.Args);
            await _controller.WaitForPlayerReadyAsync();
            _instanceServer = new InstanceIpcServer((request, token) =>
                Dispatcher.InvokeAsync(() => _controller.HandleInstanceRequestAsync(request, token),
                    System.Windows.Threading.DispatcherPriority.Normal, token).Task.Unwrap(),
                operationTimeout: TimeSpan.FromSeconds(8));
            await _instanceServer.StartAsync();
            await UpdateReadiness.NotifyReadyAsync();
            Environment.SetEnvironmentVariable(UpdateReadiness.ReadyPipeEnvironmentVariable, null);
            var rolledBack = Environment.GetEnvironmentVariable(UpdateReadiness.RolledBackEnvironmentVariable) == "1";
            Environment.SetEnvironmentVariable(UpdateReadiness.RolledBackEnvironmentVariable, null);
            if (rolledBack)
            {
                await _controller.ShowRollbackNoticeAsync();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] ERROR Startup failed: {exception}");
            MessageBox.Show(
                $"Rot could not start.\n\n{exception.Message}",
                "Rot startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Cancellation prevents queued IPC requests from mutating windows during shutdown.
        // Disposal has a bounded wait and does not need the UI dispatcher to complete.
        _instanceServer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _controller?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
