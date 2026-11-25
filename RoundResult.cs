using Microsoft.UI.Xaml;
using Windows.UI.Text;

namespace ReactionTimer;

/// <summary>
/// 成绩列表的一行。由 ItemsRepeater 的 DataTemplate 通过 x:Bind 读取。
/// 颜色一律交给 XAML 里的 ThemeResource，这里只提供数据：
/// 把 Brush 当数据传出去的话，切换主题时这些行不会跟着变。
/// </summary>
public sealed class RoundResult
{
    public string Label { get; init; }

    public string Display { get; init; }

    public FontWeight Weight { get; init; }

    public Visibility BestMark { get; init; }
}
