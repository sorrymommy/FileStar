using System.Windows;
using Velopack;

namespace FileStar;

public partial class App : Application
{
    [STAThread]
    static void Main(string[] args)
    {
        // First, before any WPF start-up: Velopack's install/update/uninstall steps run here and exit.
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
