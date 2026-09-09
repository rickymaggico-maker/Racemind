using System.Windows;
using Velopack;

namespace RaceMind;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();
        var app = new Application();
        app.Run(new MainWindow());
    }
}
