using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ChatGPTWindowsMcp;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // Only the test executable's output directory is used for configuration.
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var previous = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
        MainWindow? window = null;
        try
        {
            File.WriteAllText(configPath, "{\"SilentStartup\":true}");
            var app = new App();
            app.InitializeComponent();
            window = new MainWindow();
            app.MainWindow = window;
            Require(!window.IsVisible, "Constructing a window must not flash it on screen");
            Require(Field(window, "_tray") is not null, "Tray must be available before hiding");
            Invoke(window, "StartApplication", (object)Array.Empty<string>());
            Require(window.IsVisible, "Incomplete configuration must show even with silent startup enabled");
            var checkbox = (CheckBox)window.FindName("SilentStartupCheckBox");
            Require(checkbox.IsChecked == true, "Silent startup setting must load");
            checkbox.IsChecked = false;
            Invoke(window, "SilentStartupCheckBox_Click", checkbox, new RoutedEventArgs());
            using (var config = JsonDocument.Parse(File.ReadAllText(configPath)))
                Require(!config.RootElement.GetProperty("SilentStartup").GetBoolean(), "Setting must persist immediately");
            window.Close();
            Require(!window.IsVisible, "Close must hide to tray");
            Require(!(bool)Field(window, "_shutdownComplete")!, "Close must not shut down services");
            Invoke(window, "ShowFromTray");
            Require(window.IsVisible, "Tray must restore the window");
            window.WindowState = WindowState.Minimized;
            Invoke(window, "ShowFromTray");
            Require(window.WindowState == WindowState.Normal, "Tray must restore a minimized window");
            window.Hide();
            // Cancel before startup so this lifecycle check cannot install or connect anything.
            var savedConfig = Field(window, "_config")!;
            savedConfig.GetType().GetProperty("SilentStartup")!.SetValue(savedConfig, true);
            savedConfig.GetType().GetProperty("TunnelId")!.SetValue(savedConfig, "tunnel_smoke");
            savedConfig.GetType().GetProperty("RuntimeApiKey")!.SetValue(savedConfig, "test-only");
            ((CancellationTokenSource)Field(window, "_windowLifetime")!).Cancel();
            Invoke(window, "StartApplication", (object)Array.Empty<string>());
            Require(!window.IsVisible, "Configured silent startup must stay hidden");
            Require((int)Field(window, "_startupSequence")! > 0, "Silent startup must attempt the saved connection");
            var frame = new DispatcherFrame();
            var deadline = DateTime.UtcNow.AddSeconds(8);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) =>
            {
                if ((bool)Field(window, "_shutdownComplete")! || DateTime.UtcNow > deadline)
                    frame.Continue = false;
            };
            timer.Start();
            Invoke(window, "RequestExit");
            Dispatcher.PushFrame(frame);
            timer.Stop();
            Require((bool)Field(window, "_shutdownComplete")!, "Exit must complete cleanup while hidden");
            Require(Field(window, "_tray") is null, "Exit must release the tray icon");
            Console.WriteLine("PASS: initial visibility, setup fallback, setting persistence, hide, restore, minimized restore, silent startup, automatic connection dispatch, hidden exit and tray disposal");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            (window is null ? null : Field(window, "_tray") as IDisposable)?.Dispose();
            if (previous is null) File.Delete(configPath);
            else File.WriteAllText(configPath, previous);
        }
    }

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
