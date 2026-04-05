using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NurMarketKassa.Services;

internal static class UiPrompts
{
    public static string? PromptString(Window owner, string title, string caption, string initial = "")
    {
        var w = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x17)),
            Foreground = Brushes.White,
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock
        {
            Text = caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = Brushes.White,
        });
        var tb = new TextBox
        {
            Text = initial,
            Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
        };
        sp.Children.Add(tb);
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        string? result = null;
        var cancel = new Button
        {
            Content = "Отмена",
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true,
        };
        cancel.Click += (_, _) => { w.DialogResult = false; w.Close(); };
        var ok = new Button
        {
            Content = "OK",
            Padding = new Thickness(16, 8, 16, 8),
            IsDefault = true,
        };
        ok.Click += (_, _) =>
        {
            result = tb.Text?.Trim() ?? "";
            w.DialogResult = true;
            w.Close();
        };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        sp.Children.Add(row);
        w.Content = sp;
        tb.Focus();
        tb.SelectAll();
        return w.ShowDialog() == true ? result : null;
    }
}
