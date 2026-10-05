namespace WindhawkShare.Gui;

internal static class GuiProgram
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
#pragma warning disable WFO5001 // System light/dark theme: API still marked as experimental
        Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001
        // Used when the app restarts as administrator: reopens the same package.
        string? packageToOpen = args.Length == 2 && args[0] == "--import" ? args[1] : null;
        Application.Run(new MainForm(packageToOpen));
    }
}
