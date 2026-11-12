namespace Smena.Client;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var settings = ConnectionSettings.Load();
        if (settings is null)
        {
            using var setup = new ConnectionSetupForm(current: null);
            if (setup.ShowDialog() != DialogResult.OK || setup.Result is null)
            {
                return;
            }

            settings = setup.Result;
        }

        using var main = new MainForm(settings);
        Application.Run(main);
    }
}
