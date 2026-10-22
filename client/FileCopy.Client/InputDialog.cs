using System.Windows;
using System.Windows.Controls;

namespace FileCopy.Client;

/// <summary>Minimal single-line text prompt (used for rename / new folder).</summary>
public static class InputDialog
{
    public static string? Show(Window owner, string title, string prompt, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(3) };
        var ok = new Button { Content = "确定", Width = 80, IsDefault = true };
        var cancel = new Button { Content = "取消", Width = 80, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Content = panel,
            Owner = owner,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) =>
        {
            box.Focus();
            // Select the name without extension, like Explorer.
            int dot = initial.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : initial.Length);
        };
        return win.ShowDialog() == true && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }
}
