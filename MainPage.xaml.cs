using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ReactionTimer;

public sealed partial class MainPage : Page
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

    /// <summary>
    /// 两次输入之间的最小间隔，避免一次双击被算成两轮。
    /// Stopwatch 的刻度单位由 Frequency 决定，不能直接拿 TimeSpan 的 tick 当同一把尺子。
    /// </summary>
    private static readonly long MinActInterval = Stopwatch.Frequency * 180 / 1000;

    private static readonly string ThemeSettingPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReactionTimer",
        "theme.txt");

    private readonly ReactionSession session = new ReactionSession();
    private readonly Random random = new Random();

    private Phase phase;
    private long cueTimestamp;
    private long lastActTimestamp;
    private bool rewardOpen;
    private bool ready;

    public MainPage()
    {
        InitializeComponent();

        waitTimer.Tick += ShowCue;

        // handledEventsToo：按钮把空格吃掉之后，Esc 仍然要能冒泡到这里。
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);

        ActualThemeChanged += OnActualThemeChanged;
        Loaded += OnLoaded;
    }

    private bool IsRunning => phase == Phase.Waiting || phase == Phase.RenderingCue || phase == Phase.Ready;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ready = true;
        StartMonitorWatch();

        ApplyTheme(LoadTheme(), false);
        LoadAppIcon();
        ShowVersion();

        // 窗口的 Activated 早于本页 Loaded，那次调用被 ready 挡掉了，这里补一次首读。
        QueueMonitorUpdate();

        Reset();
        FocusTestArea();
    }

    private void LoadAppIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "NewIcon_Oz.png");

        if (File.Exists(path))
        {
            AppIconBrush.ImageSource = new BitmapImage(new Uri("file:///" + path.Replace('\\', '/')));
            return;
        }

        // 图片缺失时留一块底色，别露个空白方块。
        AppIconTile.Background = Tone("IdleSurfaceBrush");
    }

    private void ShowVersion()
    {
        // 版本号来自 csproj 的 <Version>，界面不写死，免得改版本时漏掉一处。
        string version = typeof(MainPage).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        VersionLabel.Text = $"Ver. {version}  By XueGao & oZenX_";
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (!ready)
        {
            return;
        }

        // 状态色是按主题各取一份的固定 Brush，主题换了必须重新上色。
        Refresh();
    }

    private void OnThemeMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string tag)
        {
            return;
        }

        ElementTheme theme = tag switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        ApplyTheme(theme, true);
    }

    private void ApplyTheme(ElementTheme theme, bool persist)
    {
        RequestedTheme = theme;

        if (persist)
        {
            SaveTheme(theme);
        }

        UpdateThemeMenuCheck();
    }

    private void UpdateThemeMenuCheck()
    {
        ThemeFollowSystem.IsChecked = RequestedTheme == ElementTheme.Default;
        ThemeLight.IsChecked = RequestedTheme == ElementTheme.Light;
        ThemeDark.IsChecked = RequestedTheme == ElementTheme.Dark;
    }

    private static ElementTheme LoadTheme()
    {
        try
        {
            if (File.Exists(ThemeSettingPath)
                && Enum.TryParse(File.ReadAllText(ThemeSettingPath).Trim(), out ElementTheme theme))
            {
                return theme;
            }
        }
        catch (IOException)
        {
        }

        return ElementTheme.Default;
    }

    private static void SaveTheme(ElementTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThemeSettingPath));
            File.WriteAllText(ThemeSettingPath, theme.ToString());
        }
        catch (IOException)
        {
        }
    }

    private void OnRoundSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!ready)
        {
            return;
        }

        Reset();
        FocusTestArea();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        Reset();
        FocusTestArea();
    }

    private void OnTestAreaClick(object sender, RoutedEventArgs e)
    {
        Act(Stopwatch.GetTimestamp());
    }

    private void OnTestAreaGotFocus(object sender, RoutedEventArgs e)
    {
        SignalSurface.BorderBrush = Tone("StrokeAccentBrush");
    }

    private void OnTestAreaLostFocus(object sender, RoutedEventArgs e)
    {
        SignalSurface.BorderBrush = Tone("StrokeDefaultBrush");
    }

    private void Reset()
    {
        CancelCue();
        session.Reset(ReferenceEquals(RoundSelector.SelectedItem, TenRounds) ? 10 : 5);
        phase = Phase.Idle;

        // 这里不能归零。Act() 里"完成态"分支也会走到 Reset，
        // 归零会让紧接着的第二次点击绕过防抖，多记一次抢跑。
        lastActTimestamp = Stopwatch.GetTimestamp();

        Refresh();
        QueueMonitorUpdate();
    }

    private void Act(long timestamp)
    {
        if (rewardOpen)
        {
            return;
        }

        if (lastActTimestamp != 0 && timestamp - lastActTimestamp < MinActInterval)
        {
            return;
        }

        lastActTimestamp = timestamp;

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

    private void Refresh()
    {
        RefreshScores();

        bool idle = phase == Phase.Idle || phase == Phase.Complete;
        RoundSelector.IsEnabled = idle;
        ResetButton.IsEnabled = phase != Phase.Idle;

        // 焦点描边是单独的刷子，切主题时要跟着一起换，否则浅色下会留一条亮蓝边。
        SignalSurface.BorderBrush = Tone(
            TestArea.FocusState == FocusState.Unfocused ? "StrokeDefaultBrush" : "StrokeAccentBrush");

        switch (phase)
        {
            case Phase.Idle:
                PaintNeutral("开始测试", "点击此处或按空格");
                break;

            case Phase.Waiting:
                // 等待期用警示红，和"现在！"的绿色形成强对比。
                PaintSignal("CriticalSurfaceBrush", "CriticalFillBrush", "CriticalFillBrush");
                SetSignal("等待变绿", "第 " + (session.Times.Count + 1) + " 轮");
                break;

            case Phase.FalseStart:
                PaintSignal("CautionSurfaceBrush", "CautionFillBrush", "CautionFillBrush");
                SetSignal("抢跑了", "点击或按空格，重试这一轮");
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
                // 绿信号期间切主题也会走到这里，颜色要按新主题重上一次。
                PaintSignal("SuccessSurfaceBrush", "SuccessFillBrush", "SuccessFillBrush");
                break;
        }
    }

    private void RefreshScores()
    {
        ProgressText.Text = $"已完成 {session.Times.Count} / {session.RoundLimit} 轮";
        ProgressMeter.Maximum = session.RoundLimit;
        ProgressMeter.Value = session.Times.Count;
        AverageText.Text = FormatMilliseconds(session.Average);
        BestText.Text = FormatMilliseconds(session.Best);
        FalseStartsText.Text = $"抢跑 {session.FalseStarts} 次";

        List<RoundResult> rows = new List<RoundResult>(session.RoundLimit);

        for (int i = 0; i < session.RoundLimit; i++)
        {
            bool hasResult = i < session.Times.Count;
            bool isBest = hasResult && session.Times[i] == session.Best;

            rows.Add(new RoundResult
            {
                Label = (i + 1).ToString("00", CultureInfo.InvariantCulture),
                Display = hasResult ? FormatMilliseconds(session.Times[i]) : "—",
                Weight = isBest ? FontWeights.SemiBold : FontWeights.Normal,
                BestMark = isBest ? Visibility.Visible : Visibility.Collapsed
            });
        }

        RoundResults.ItemsSource = rows;
    }

    private void PaintNeutral(string title, string hint)
    {
        PaintSignal("IdleSurfaceBrush", "IdleDotBrush", "TitlePrimaryBrush");
        SetSignal(title, hint);
    }

    private void PaintSignal(string surfaceKey, string fillKey, string titleKey)
    {
        SignalSurface.Background = Tone(surfaceKey);
        SignalDot.Fill = Tone(fillKey);
        SignalTitle.Foreground = Tone(titleKey);
        SignalHint.Foreground = Tone("TextSecondaryBrush");
    }

    private void SetSignal(string title, string hint)
    {
        SignalTitle.Text = title;
        SignalHint.Text = hint;
        AutomationProperties.SetName(TestArea, title);
    }

    private void FocusTestArea()
    {
        TestArea.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// 先查当前主题的字典（六个状态色明暗各一份），
    /// 查不到再落到不随主题变化的系统刷子别名。
    /// </summary>
    private Brush Tone(string key)
    {
        string themeKey = ActualTheme == ElementTheme.Light ? "Light" : "Default";

        if (Resources.ThemeDictionaries.TryGetValue(themeKey, out object raw)
            && raw is ResourceDictionary themed
            && themed.TryGetValue(key, out object value)
            && value is Brush fromTheme)
        {
            return fromTheme;
        }

        if (Resources.TryGetValue(key, out object fallback) && fallback is Brush brush)
        {
            return brush;
        }

        // 资源缺失时宁可画成透明，也不要让一次取色把整页打死。
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    private static string FormatMilliseconds(int value)
    {
        if (value < 0)
        {
            return "—";
        }

        return value.ToString(CultureInfo.InvariantCulture) + " ms";
    }
}
