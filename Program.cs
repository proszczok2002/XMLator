namespace XMLator;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (UsbWorker.RunIfRequested(args))
            return;
        var (ready, failed, profile) = UpdateInstaller.StartupArguments(args);
        using (ready)
        {
            ApplicationConfiguration.Initialize();
            var preferences = new InterfacePreferences(Path.Combine(AppContext.BaseDirectory, "config.ini"));
            using var wizard = new Wizard(profile, preferences, UpdateService.ForCurrentExecutable(), scheduleAutomaticUpdate: !failed);
            if (ready is not null)
                wizard.Shown += (_, _) => ready.Set();
            if (failed)
                wizard.Shown += (_, _) => wizard.ShowUpdateFailure();
            Application.Run(wizard);
        }
    }
}
