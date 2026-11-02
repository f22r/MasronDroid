using System.Windows;
using Velopack;

namespace MasronDroid;

public partial class App : Application { }

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
