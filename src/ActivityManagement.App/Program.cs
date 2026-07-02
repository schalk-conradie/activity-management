using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace ActivityManagement.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => StartupRegistration.Enable())
            .OnAfterUpdateFastCallback(_ => StartupRegistration.Enable())
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.Disable())
            .Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
