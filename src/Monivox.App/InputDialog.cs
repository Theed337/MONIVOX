using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Monivox.App;

/// <summary>Kleine Dialoge (Text eingeben, aus Liste wählen) im MONIVOX-Stil.</summary>
public static class InputDialog
{
    private static Window CreateWindow(Window owner, string title, double height) => new()
    {
        Title = title,
        Owner = owner,
        Width = 460,
        Height = height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ResizeMode = ResizeMode.NoResize,
        Style = (Style)Application.Current.Resources["AppWindow"],
        ShowInTaskbar = false,
    };

    public static string? Ask(Window owner, string title, string prompt, string initial)
    {
        var w = CreateWindow(owner, title, 220);
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 12) };
        var ok = new Button { Content = "OK", IsDefault = true, Style = (Style)Application.Current.Resources["Primary"] };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true };
        ok.Click += (_, _) => w.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        w.Content = panel;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.SourceInitialized += (_, _) => Theme.ApplyWindowChrome(w);
        return w.ShowDialog() == true ? box.Text.Trim() : null;
    }

    public static string? Choose(Window owner, string title, string prompt, IReadOnlyList<(string Label, string Value)> items)
    {
        var w = CreateWindow(owner, title, 460);
        var list = new ListBox
        {
            ItemsSource = items.Select(i => i.Label).ToList(),
            Height = 300,
            Margin = new Thickness(0, 8, 0, 12),
        };
        var ok = new Button { Content = "Übernehmen", IsDefault = true, Style = (Style)Application.Current.Resources["Primary"] };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true };
        ok.Click += (_, _) => { if (list.SelectedIndex >= 0) w.DialogResult = true; };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedIndex >= 0) w.DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(list);
        panel.Children.Add(buttons);
        w.Content = panel;
        w.SourceInitialized += (_, _) => Theme.ApplyWindowChrome(w);
        return w.ShowDialog() == true ? items[list.SelectedIndex].Value : null;
    }
}
