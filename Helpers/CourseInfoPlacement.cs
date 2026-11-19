using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace EveningSelfStudyClock.Helpers;

/// <summary>在主体区和底部之间移动同一个课程控件，保留绑定和玻璃目标。</summary>
public static class CourseInfoPlacement
{
    public static void Apply(Control course, Grid body, Grid footer, bool between, bool compact)
    {
        var destination = between ? body : footer;
        if (!ReferenceEquals(course.Parent, destination))
        {
            (course.Parent as Panel)?.Children.Remove(course);
            course.Margin = between ? default : new Thickness(24, 0);
            destination.Children.Add(course);
        }
        course.VerticalAlignment = between ? VerticalAlignment.Top : VerticalAlignment.Center;
        Grid.SetRow(course, 0);
        Grid.SetColumn(course, between || compact ? 0 : 1);
        Grid.SetColumnSpan(course, !between && compact ? 3 : 1);
    }

    public static double BetweenTop(double clockBottom, double lowerBoundary, double height, double gap, double percent)
    {
        var earliest = clockBottom + gap;
        var latest = Math.Max(earliest, lowerBoundary - height - gap);
        return earliest + (latest - earliest) * Math.Clamp(percent, 0, 100) / 100;
    }
}
