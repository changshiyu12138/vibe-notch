using Velopack;

namespace VibeNotch;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetAppUserModelId(ToastService.Aumid)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
