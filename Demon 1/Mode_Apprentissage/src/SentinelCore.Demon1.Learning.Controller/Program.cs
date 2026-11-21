namespace SentinelCore.Demon1.Learning.Controller;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--worker") return global::CollectorRuntime.Run(args.Skip(1).ToArray());
        ApplicationConfiguration.Initialize();
        var auth = new AuthStore();
        var firstLaunch = !auth.IsConfigured;
        if (firstLaunch)
        {
            using var setup = new MasterPasswordDialog(isSetup: true);
            if (setup.ShowDialog() != DialogResult.OK || string.IsNullOrEmpty(setup.Password)) return 0;
            auth.SetPassword(setup.Password);
        }
        else
        {
            var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SentinelCore", "Demon1Learning");
            var settings = LearningSettings.Load(dataRoot);
            using var authentication = new StartupAuthDialog(auth, dataRoot, settings);
            if (authentication.ShowDialog() != DialogResult.OK) return 0;
        }
        Application.Run(new MainForm(auth));
        return 0;
    }
}
