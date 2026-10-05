namespace WindhawkShare.Gui;

internal static class GuiProgram
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
#pragma warning disable WFO5001 // Tema chiaro/scuro di sistema: API ancora segnata come sperimentale
        Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001
        Application.Run(new MainForm());
    }
}
