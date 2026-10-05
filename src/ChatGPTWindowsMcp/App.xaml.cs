using System.Windows;

namespace ChatGPTWindowsMcp;

public partial class App : Application
{
    internal static LogSink Log { get; } = new();
    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.EnsureDirectories();
        AppPaths.RemoveEmptyWorkingDirectories();
        Log.Write("session", $"启动程序；版本 {typeof(App).Assembly.GetName().Version}；.NET {Environment.Version}；{Environment.OSVersion}；目录 {AppPaths.BaseDirectory}");
        DispatcherUnhandledException += (_, args) => Log.Error("ui-unhandled", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) Log.Error("unhandled", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Error("task-unobserved", args.Exception);
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.StartApplication(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Write("session", $"程序退出；退出码 {e.ApplicationExitCode}。");
        base.OnExit(e);
    }
}
