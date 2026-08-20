using System.Windows;

namespace ChatGPTWindowsMcp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.EnsureDirectories();
        base.OnStartup(e);
    }
}
