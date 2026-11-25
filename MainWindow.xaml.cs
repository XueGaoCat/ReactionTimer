using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using Color = Windows.UI.Color;

namespace ReactionTimer;

public sealed partial class MainWindow : Window
{
    private const int ClientWidth = 1040;
    private const int ClientHeight = 700;
    private const int MinimumWidth = 880;
    private const int MinimumHeight = 620;

    public MainWindow()
    {
        InitializeComponent();

        ConfigureBackdrop();
        ConfigureTitleBar();
        ConfigureSize();

        // 页面切主题时同步系统按钮的颜色，否则浅色下会变成白字白底。
        RootPage.ActualThemeChanged += (sender, args) => UpdateTitleBarTheme(RootPage.ActualTheme);

        AppWindow.Changed += OnAppWindowChanged;
        Activated += OnActivated;
        Closed += OnClosed;
    }

    private void ConfigureBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            return;
        }

        if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
            return;
        }

        // 没有系统背景材质时给一块纯色，避免透成黑色。
        RootPage.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0x20, 0x20));
    }

    private void ConfigureTitleBar()
    {
        // 内容延伸到标题栏，标题栏区域跟着 Mica 透明，只有拖拽命中和系统按钮。
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(RootPage.AppTitleBar);

        UpdateTitleBarTheme(RootPage.ActualTheme);
    }

    private void UpdateTitleBarTheme(ElementTheme theme)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        bool light = theme == ElementTheme.Light;

        Color foreground = light
            ? Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)
            : Color.FromArgb(0xFF, 0xE9, 0xED, 0xF6);
        Color inactiveForeground = light
            ? Color.FromArgb(0xFF, 0x5D, 0x5D, 0x5D)
            : Color.FromArgb(0xFF, 0x79, 0x86, 0x9F);

        AppWindowTitleBar bar = AppWindow.TitleBar;
        bar.BackgroundColor = Colors.Transparent;
        bar.InactiveBackgroundColor = Colors.Transparent;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = inactiveForeground;
        bar.ButtonHoverForegroundColor = light ? Colors.Black : Colors.White;
        bar.ButtonHoverBackgroundColor = light
            ? Color.FromArgb(0x18, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
        bar.ButtonPressedForegroundColor = light ? Colors.Black : Colors.White;
        bar.ButtonPressedBackgroundColor = light
            ? Color.FromArgb(0x28, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF);
    }

    private void ConfigureSize()
    {
        // AppWindow 用的是物理像素，XAML 用的是 DIP，按当前 DPI 换算。
        IntPtr handle = WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(handle) / 96.0;

        AppWindow.ResizeClient(new SizeInt32(
            (int)Math.Round(ClientWidth * scale),
            (int)Math.Round(ClientHeight * scale)));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(MinimumWidth * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(MinimumHeight * scale);
        }

        CenterOnScreen();

        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "ReactionTimer.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }
    }

    private void CenterOnScreen()
    {
        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        SizeInt32 size = AppWindow.Size;

        AppWindow.Move(new PointInt32(
            area.WorkArea.X + ((area.WorkArea.Width - size.Width) / 2),
            area.WorkArea.Y + ((area.WorkArea.Height - size.Height) / 2)));
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // 跨屏拖动或改缩放会改变所在显示器，刷新率也要重读。
        if (args.DidPositionChange || args.DidSizeChange)
        {
            RootPage.QueueMonitorUpdate();
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        RootPage.HandleWindowActivation(args.WindowActivationState);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        RootPage.Shutdown();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
