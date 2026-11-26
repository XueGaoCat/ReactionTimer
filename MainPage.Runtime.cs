using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using WinRT.Interop;

namespace ReactionTimer;

public sealed partial class MainPage
{
    private readonly DispatcherTimer waitTimer = new DispatcherTimer();
    private readonly DispatcherTimer monitorTimer = new DispatcherTimer();

    private int monitorReadVersion;

    private BitmapImage rewardImage;

    /// <summary>
    /// 每帧回调，用来确认"现在！"这一帧已经真正提交，再开始计时。
    /// </summary>
    private void ShowCue(object sender, object e)
    {
        waitTimer.Stop();
        phase = Phase.RenderingCue;

        // 绿信号是本程序唯一要"看见"的视觉事件，用高饱和前景色而不是主题填充色。
        PaintSignal("SuccessSurfaceBrush", "SuccessFillBrush", "SuccessFillBrush");
        SetSignal("现在！", "点击或按空格");

        // 把渲染延迟算进反应时间是致命误差，等第一帧落地再取时间戳。
        CompositionTarget.Rendering += OnFirstCueFrame;
    }

    private void OnFirstCueFrame(object sender, object e)
    {
        CompositionTarget.Rendering -= OnFirstCueFrame;
        cueTimestamp = Stopwatch.GetTimestamp();
        phase = Phase.Ready;
    }

    private void CancelCue()
    {
        waitTimer.Stop();
        CompositionTarget.Rendering -= OnFirstCueFrame;
    }

    private void StartMonitorWatch()
    {
        monitorTimer.Interval = TimeSpan.FromMilliseconds(180);
        monitorTimer.Tick += OnMonitorTick;
    }

    /// <summary>
    /// 窗口移动、缩放、激活、显示配置变化都会改变目标显示器，合并成一次读取。
    /// </summary>
    internal void QueueMonitorUpdate()
    {
        if (!ready)
        {
            return;
        }

        if (!monitorTimer.IsEnabled)
        {
            monitorTimer.Start();
        }
    }

    private async void OnMonitorTick(object sender, object e)
    {
        monitorTimer.Stop();

        if (!ready)
        {
            return;
        }

        int version = ++monitorReadVersion;
        IntPtr handle = WindowNative.GetWindowHandle(App.MainWindow);

        // CCD 查询要跨到 user32，放后台线程避免卡住动画。
        double hz = await Task.Run(() => MonitorRefresh.ReadHz(handle));

        if (version != monitorReadVersion)
        {
            return;
        }

        MonitorRate.Text = hz > 0.0
            ? hz.ToString("0.##", CultureInfo.CurrentCulture) + " Hz"
            : "—";
    }

    internal void HandleWindowActivation(WindowActivationState state)
    {
        if (!ready)
        {
            return;
        }

        if (state == WindowActivationState.Deactivated)
        {
            // 切走窗口时信号还亮着，测出来的就是切窗口的时间，直接作废这一轮。
            if (IsRunning)
            {
                CancelCue();
                phase = Phase.Paused;
                Refresh();
            }

            return;
        }

        QueueMonitorUpdate();
    }

    internal void Shutdown()
    {
        // 关窗后仍可能有已排队的 tick 跑完 await 回来，这里把 ready 关掉当最后一道闸。
        ready = false;
        CancelCue();
        waitTimer.Stop();
        monitorTimer.Stop();
        monitorReadVersion++;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;

        // 长按 Esc 只重置一次。
        if (e.KeyStatus.RepeatCount > 1 || rewardOpen)
        {
            return;
        }

        Reset();
        FocusTestArea();
    }

    private async void ShowReward()
    {
        rewardOpen = true;

        ContentDialog dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "彩蛋已解锁",
            PrimaryButtonText = session.IsComplete ? "查看成绩" : "继续测试",
            DefaultButton = ContentDialogButton.Primary,
            Content = BuildRewardContent()
        };

        ApplyDefaultDialogStyle(dialog);

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // async void 里漏出去就是直接杀进程，彩蛋打不开也不该连累整个人测。
            App.LogCrash(ex);
        }
        finally
        {
            rewardOpen = false;
            FocusTestArea();
        }
    }

    /// <summary>
    /// WinUI 3 的历史缺陷（microsoft-ui-xaml#8476）：
    /// 代码里 new 出来的 ContentDialog 不会自动套用 DefaultContentDialogStyle，
    /// 内部 Popup 的 ContentDialogOpenCloseThemeTransition 因此没挂上，
    /// 表现就是"有关闭动画、没有打开动画"。显式套一次默认样式即可恢复。
    /// 该问题从 1.3 一直到 1.7 都还在，官方只给了这个 workaround。
    /// </summary>
    private static void ApplyDefaultDialogStyle(ContentDialog dialog)
    {
        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object value)
            && value is Style style)
        {
            dialog.Style = style;
        }
    }

    private FrameworkElement BuildRewardContent()
    {
        // 2.6 MB 的图只解码一次，之后的弹窗直接复用。
        if (rewardImage == null)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "EasterEgg.png");
            rewardImage = new BitmapImage(new Uri("file:///" + path.Replace('\\', '/')));
        }

        Image image = new Image
        {
            Height = 300,
            Stretch = Stretch.Uniform,
            Source = rewardImage
        };

        TextBlock score = new TextBlock
        {
            Text = FormatMilliseconds(session.Times[session.Times.Count - 1]),
            FontSize = 46,
            FontWeight = FontWeights.SemiBold,
            Foreground = Tone("AccentBrush")
        };

        TextBlock round = new TextBlock
        {
            Text = "第 " + session.Times.Count + " 轮",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Tone("TextSecondaryBrush")
        };

        StackPanel text = new StackPanel
        {
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Children.Add(score);
        text.Children.Add(round);

        Grid content = new Grid { ColumnSpacing = 20 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        content.Children.Add(image);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        return content;
    }
}
