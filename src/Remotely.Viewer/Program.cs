namespace Remotely.Viewer;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            return 1;
        }
    }
}
