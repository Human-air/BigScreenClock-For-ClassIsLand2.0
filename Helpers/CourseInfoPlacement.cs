using Avalonia.Controls;

namespace EveningSelfStudyClock.Helpers;

/// <summary>在保留同一个课程控件的前提下切换位置，兼容半屏布局及玻璃跟随。</summary>
public static class CourseInfoPlacement
{
    public static void Apply(Control course, Grid header, Grid footer, bool atTop, bool compact)
    {
        var destination = atTop ? header : footer;
        if (!ReferenceEquals(course.Parent, destination))
        {
            (course.Parent as Panel)?.Children.Remove(course);
            destination.Children.Add(course);
        }
        if (atTop)
            while (header.RowDefinitions.Count < 3) header.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        if (!atTop && header.RowDefinitions.Count == 3
            && !header.Children.Any(child => Grid.GetRow(child) >= 2))
            header.RowDefinitions.RemoveAt(2);
        Grid.SetRow(course, atTop ? 2 : 0);
        Grid.SetColumn(course, atTop || compact ? 0 : 1);
        Grid.SetColumnSpan(course, atTop || compact ? 3 : 1);
    }
}
