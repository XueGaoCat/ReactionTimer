using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace ReactionTimer;

public partial class App : Application
{
    /// <summary>
    /// 单窗口应用，控制器需要从这里拿到 HWND 与 AppWindow。
    /// </summary>
    internal static MainWindow MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            throw;
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception ?? new Exception(e.Message));
    }

    internal static void LogCrash(Exception ex)
    {
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "ratingtest_crash.log"), ex.ToString());
        }
        catch (Exception)
        {
        }
    }
}
