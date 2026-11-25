using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ReactionTimer;

internal sealed class ReactionController
{
    private enum Phase
    {
        Idle,
        Waiting,
        RenderingCue,
        Ready,
        FalseStart,
        Paused,
        Feedback,
        Complete
    }

    private readonly Window window;
    private readonly ReactionSession session = new ReactionSession();
    private readonly DispatcherTimer waitTimer = new DispatcherTimer(DispatcherPriority.Background);
    private readonly DispatcherTimer monitorTimer = new DispatcherTimer(DispatcherPriority.Background);
    private readonly Random random = new Random();

    private readonly RadioButton fiveRounds;
    private readonly RadioButton tenRounds;
    private readonly Button testArea;
    private readonly Button resetButton;
    private readonly Border signalHalo;
    private readonly Ellipse signalDot;
    private readonly TextBlock signalTitle;
    private readonly TextBlock signalHint;
    private readonly Grid mainContent;
    private readonly Grid roundResults;
    private readonly TextBlock progressText;
    private readonly TextBlock monitorRate;
    private readonly ProgressBar progressMeter;
    private readonly TextBlock averageText;
    private readonly TextBlock bestText;
    private readonly TextBlock falseStartsText;
    private readonly Border rewardOverlay;
    private readonly Image rewardImage;
    private readonly TextBlock rewardScore;
    private readonly TextBlock rewardRound;
    private readonly Button closeReward;

    private Phase phase;
    private long cueTimestamp;
    private int monitorReadVersion;

    private bool IsRunning => phase == Phase.Waiting || phase == Phase.RenderingCue || phase == Phase.Ready;

    private bool RewardVisible => rewardOverlay.Visibility == Visibility.Visible;

    internal ReactionController(Window window)
    {
        this.window = window;

        fiveRounds = Find<RadioButton>("FiveRounds");
        tenRounds = Find<RadioButton>("TenRounds");
        testArea = Find<Button>("TestArea");
        resetButton = Find<Button>("ResetButton");
        signalHalo = Find<Border>("SignalHalo");
        signalDot = Find<Ellipse>("SignalDot");
        signalTitle = Find<TextBlock>("SignalTitle");
        signalHint = Find<TextBlock>("SignalHint");
        mainContent = Find<Grid>("MainContent");
        roundResults = Find<Grid>("RoundResults");
        progressText = Find<TextBlock>("ProgressText");
        monitorRate = Find<TextBlock>("MonitorRate");
        progressMeter = Find<ProgressBar>("ProgressMeter");
        averageText = Find<TextBlock>("AverageText");
        bestText = Find<TextBlock>("BestText");
        falseStartsText = Find<TextBlock>("FalseStartsText");
        rewardOverlay = Find<Border>("RewardOverlay");
        rewardImage = Find<Image>("RewardImage");
        rewardScore = Find<TextBlock>("RewardScore");
        rewardRound = Find<TextBlock>("RewardRound");
        closeReward = Find<Button>("CloseReward");

        window.Icon = Program.LoadImage("ReactionTimer.ico");

        monitorTimer.Interval = TimeSpan.FromMilliseconds(180);
        monitorTimer.Tick += UpdateMonitorRate;

        window.SourceInitialized += (object sender, EventArgs e) =>
        {
            UseDarkTitleBar(window);
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).AddHook(OnDisplayMessage);
            QueueMonitorUpdate();
        };

        window.LocationChanged += (object sender, EventArgs e) => QueueMonitorUpdate();
        window.SizeChanged += (object sender, SizeChangedEventArgs e) => QueueMonitorUpdate();
        window.Activated += (object sender, EventArgs e) => QueueMonitorUpdate();

        window.Loaded += (object sender, RoutedEventArgs e) => testArea.Focus();

        window.Deactivated += (object sender, EventArgs e) =>
        {
            if (IsRunning)
            {
                CancelCue();
                phase = Phase.Paused;
                Refresh();
            }
        };

        window.Closed += (object sender, EventArgs e) =>
        {
            CancelCue();
            monitorTimer.Stop();
            monitorReadVersion++;
        };

        window.PreviewKeyDown += OnKeyDown;
        waitTimer.Tick += ShowCue;

        fiveRounds.Checked += (object sender, RoutedEventArgs e) =>
        {
            Reset();
            testArea.Focus();
        };

        tenRounds.Checked += (object sender, RoutedEventArgs e) =>
        {
            Reset();
            testArea.Focus();
        };

        resetButton.Click += (object sender, RoutedEventArgs e) =>
        {
            Reset();
            testArea.Focus();
        };

        testArea.PreviewMouseLeftButtonDown += OnMouseDown;
        testArea.Click += (object sender, RoutedEventArgs e) => Act(Stopwatch.GetTimestamp());

        closeReward.Click += (object sender, RoutedEventArgs e) => HideReward();

        Reset();
    }

    private T Find<T>(string name) where T : class
    {
        return (T)window.FindName(name);
    }

    private IntPtr OnDisplayMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_DISPLAYCHANGE / WM_SETTINGCHANGE / WM_DPICHANGED
        if (message == 0x7E || message == 0x1A || message == 0x02E0)
        {
            QueueMonitorUpdate();
        }

        return IntPtr.Zero;
    }

    private void QueueMonitorUpdate()
    {
        if (!monitorTimer.IsEnabled)
        {
            monitorTimer.Start();
        }
    }

    private async void UpdateMonitorRate(object sender, EventArgs e)
    {
        monitorTimer.Stop();

        int version = ++monitorReadVersion;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        double hz = await Task.Run(() => MonitorRefresh.ReadHz(handle));

        if (version == monitorReadVersion)
        {
            monitorRate.Text = hz > 0.0
                ? hz.ToString("0.##", CultureInfo.CurrentCulture) + " Hz"
                : "—";
        }
    }

    private void Reset()
    {
        CancelCue();
        session.Reset(tenRounds.IsChecked == true ? 10 : 5);
        phase = Phase.Idle;
        Refresh();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        long timestamp = Stopwatch.GetTimestamp();
        e.Handled = true;

        // 单击生效，双击不重复计入，避免一次操作被当作两轮。
        if (e.ClickCount == 1 && !RewardVisible)
        {
            testArea.Focus();
            Act(timestamp);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        long timestamp = Stopwatch.GetTimestamp();

        if (e.Key == Key.Escape)
        {
            e.Handled = true;

            if (RewardVisible)
            {
                HideReward();
            }
            else if (!e.IsRepeat)
            {
                Reset();
                testArea.Focus();
            }
            return;
        }

        if (e.Key != Key.Space || (!RewardVisible && !IsRunning && !testArea.IsKeyboardFocusWithin))
        {
            return;
        }

        e.Handled = true;

        if (e.IsRepeat)
        {
            return;
        }

        if (RewardVisible)
        {
            HideReward();
        }
        else
        {
            Act(timestamp);
        }
    }

    private void Act(long timestamp)
    {
        if (RewardVisible)
        {
            return;
        }

        if (phase == Phase.Waiting || phase == Phase.RenderingCue)
        {
            // 信号还没出现就响应，记为抢跑。
            CancelCue();
            session.RecordFalseStart();
            phase = Phase.FalseStart;
            Refresh();
            return;
        }

        if (phase == Phase.Ready)
        {
            double elapsed = (double)(timestamp - cueTimestamp) * 1000.0 / Stopwatch.Frequency;
            bool fastEnough = session.RecordReaction(Math.Max(0.0, elapsed));

            phase = session.IsComplete ? Phase.Complete : Phase.Feedback;
            Refresh();

            if (fastEnough)
            {
                ShowReward();
            }
            return;
        }

        if (phase == Phase.Complete)
        {
            Reset();
        }

        phase = Phase.Waiting;
        Refresh();

        waitTimer.Interval = TimeSpan.FromMilliseconds(random.Next(1350, 4301));
        waitTimer.Start();
    }

    private void CancelCue()
    {
        waitTimer.Stop();
        CompositionTarget.Rendering -= OnFirstCueFrame;
    }

    private void ShowCue(object sender, EventArgs e)
    {
        waitTimer.Stop();
        phase = Phase.RenderingCue;
        PaintSignal("现在！", "点击或按空格", "#70E4AF", "#9CEFCB", "#A8F3D0", "#166744", "#102C20", "#1B4736");

        // 等第一帧真正画出来再开始计时，否则把渲染延迟算成反应时间。
        CompositionTarget.Rendering += OnFirstCueFrame;
    }

    private void OnFirstCueFrame(object sender, EventArgs e)
    {
        cueTimestamp = Stopwatch.GetTimestamp();
        phase = Phase.Ready;
        CompositionTarget.Rendering -= OnFirstCueFrame;
    }

    private void Refresh()
    {
        RefreshScores();

        bool idle = phase == Phase.Idle || phase == Phase.Complete;
        fiveRounds.IsEnabled = idle;
        tenRounds.IsEnabled = idle;
        resetButton.IsEnabled = phase != Phase.Idle;

        switch (phase)
        {
            case Phase.Idle:
                PaintNeutral("开始测试", "点击此处或按空格");
                break;
            case Phase.Waiting:
                PaintSignal("等待变绿", "第 " + (session.Times.Count + 1) + " 轮", "#462832", "#6B3A4B", "#623341", "#FDA5B6", "#FFF1F4", "#F2B3C2");
                break;
            case Phase.FalseStart:
                PaintSignal("抢跑了", "点击或按空格，重试这一轮", "#3E3029", "#664836", "#594032", "#F4BC84", "#FFF0E2", "#EFC8A6");
                break;
            case Phase.Paused:
                PaintNeutral("测试已暂停", "点击或按空格，重新开始这一轮");
                break;
            case Phase.Feedback:
                PaintNeutral(FormatMilliseconds(session.Times[session.Times.Count - 1]), "点击或按空格，进入下一轮");
                break;
            case Phase.Complete:
                PaintNeutral("测试完成", "点击或按空格，再测一组");
                break;
            case Phase.RenderingCue:
            case Phase.Ready:
                break;
        }
    }

    private void RefreshScores()
    {
        progressText.Text = $"已完成 {session.Times.Count} / {session.RoundLimit} 轮";
        progressMeter.Maximum = session.RoundLimit;
        progressMeter.Value = session.Times.Count;
        averageText.Text = FormatMilliseconds(session.Average);
        bestText.Text = FormatMilliseconds(session.Best);
        falseStartsText.Text = $"抢跑 {session.FalseStarts} 次";

        roundResults.Children.Clear();
        roundResults.RowDefinitions.Clear();

        for (int i = 0; i < session.RoundLimit; i++)
        {
            bool hasResult = i < session.Times.Count;
            bool isBest = hasResult && session.Times[i] == session.Best;

            TextBlock index = new TextBlock
            {
                Text = (i + 1).ToString("00", CultureInfo.InvariantCulture),
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Tone(hasResult ? "#B2BDD2" : "#79869F")
            };

            TextBlock time = new TextBlock
            {
                Text = hasResult ? FormatMilliseconds(session.Times[i]) : "—",
                FontWeight = isBest ? FontWeights.SemiBold : FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = Tone(isBest ? "#9BC5FF" : hasResult ? "#E9EDF6" : "#79869F")
            };

            DockPanel row = new DockPanel
            {
                LastChildFill = true,
                Margin = new Thickness(8, 0, 8, 0)
            };
            row.Children.Add(index);
            row.Children.Add(time);

            Border cell = new Border
            {
                Background = Tone(isBest ? "#25384D" : i % 2 == 0 ? "#222630" : "#1B1E26"),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 1),
                Child = row
            };

            roundResults.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            Grid.SetRow(cell, i);
            roundResults.Children.Add(cell);
        }
    }

    private void PaintNeutral(string title, string hint)
    {
        PaintSignal(title, hint, "#252D3D", "#41536D", "#334968", "#9BC5FF", "#E9EDF6", "#B6C1D8");
    }

    private void PaintSignal(string title, string hint, string surface, string outline, string halo, string dot, string titleColor, string hintColor)
    {
        testArea.Background = Tone(surface);
        testArea.BorderBrush = Tone(outline);
        signalHalo.Background = Tone(halo);
        signalDot.Fill = Tone(dot);
        signalTitle.Text = title;
        signalTitle.Foreground = Tone(titleColor);
        signalHint.Text = hint;
        signalHint.Foreground = Tone(hintColor);
        AutomationProperties.SetName(testArea, title);
    }

    private void ShowReward()
    {
        if (rewardImage.Source == null)
        {
            rewardImage.Source = Program.LoadImage("EasterEgg.png");
        }

        rewardScore.Text = FormatMilliseconds(session.Times[session.Times.Count - 1]);
        rewardRound.Text = "第 " + session.Times.Count + " 轮";
        closeReward.Content = session.IsComplete ? "查看成绩" : "继续测试";

        mainContent.IsEnabled = false;
        rewardOverlay.Visibility = Visibility.Visible;

        if (SystemParameters.ClientAreaAnimation)
        {
            rewardOverlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
            {
                FillBehavior = FillBehavior.Stop
            });
        }

        closeReward.Focus();
    }

    private void HideReward()
    {
        rewardOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        rewardOverlay.Visibility = Visibility.Collapsed;
        mainContent.IsEnabled = true;
        testArea.Focus();
    }

    private static SolidColorBrush Tone(string hex)
    {
        SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static string FormatMilliseconds(int value)
    {
        if (value < 0)
        {
            return "—";
        }
        return value.ToString(CultureInfo.InvariantCulture) + " ms";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaUseImmersiveDarkMode = 20;

    private static void UseDarkTitleBar(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        int enabled = 1;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }
}
