using System.Windows;

namespace ZeppieSays.UI;

/// <summary>The big heading above a sidebar page; the nav style defaults it to the menu label.</summary>
public static class Nav
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.RegisterAttached(
        "Title", typeof(string), typeof(Nav), new PropertyMetadata(""));

    public static string GetTitle(DependencyObject o) => (string)o.GetValue(TitleProperty);
    public static void SetTitle(DependencyObject o, string value) => o.SetValue(TitleProperty, value);
}
