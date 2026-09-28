using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Dlss5Optimizer.App.ViewModels;

namespace Dlss5Optimizer.App;

public interface IDialogs
{
    bool Confirm(string title, string text, bool warning = false);
    bool ConfirmList(string title, string header, IEnumerable<string> lines);
    void ShowList(string title, string header, IEnumerable<string> lines);
    void Info(string title, string text);
}

public sealed class Dialogs(Func<Window?> owner) : IDialogs
{
    public bool Confirm(string title, string text, bool warning = false) =>
        MessageBox.Show(owner()!, text, title, MessageBoxButton.YesNo, warning ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Info(string title, string text) =>
        MessageBox.Show(owner()!, text, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowList(string title, string header, IEnumerable<string> lines) => ListWindow(title, header, lines, confirm: false);

    /// <summary>Bestätigung mit scrollbarer Liste (Installationsschritte, Downloads).</summary>
    public bool ConfirmList(string title, string header, IEnumerable<string> lines) => ListWindow(title, header, lines, confirm: true);

    private bool ListWindow(string title, string header, IEnumerable<string> lines, bool confirm)
    {
        var window = new Window
        {
            Title = title,
            Owner = owner(),
            Width = 720,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.Resources["BackgroundBrush"],
            Foreground = (Brush)Application.Current.Resources["TextBrush"],
        };
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), FontSize = 14 };
        var list = new TextBox
        {
            Text = string.Join(Environment.NewLine, lines),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Padding = new Thickness(8),
        };
        Grid.SetRow(list, 1);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = confirm ? "Fortfahren" : "Schließen", IsDefault = true, Style = (Style)Application.Current.Resources["PrimaryButton"], Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(ok);
        if (confirm)
            buttons.Children.Add(new Button { Content = "Abbrechen", IsCancel = true });
        Grid.SetRow(buttons, 2);

        grid.Children.Add(head);
        grid.Children.Add(list);
        grid.Children.Add(buttons);
        window.Content = grid;
        return window.ShowDialog() == true;
    }
}

/// <summary>Null oder leerer Text → Collapsed.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool empty = value is null || value is string s && string.IsNullOrWhiteSpace(s)
                     || value is System.Collections.ICollection c && c.Count == 0;
        bool invert = parameter as string == "invert";
        return empty ^ invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (parameter as string == "invert")
            b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StatusKind.Good => "AccentBrush",
            StatusKind.Warning => "WarningBrush",
            StatusKind.Blocked => "ErrorBrush",
            StatusKind.Installed => "InfoBrush",
            _ => "MutedBrush",
        };
        return Application.Current.Resources[key];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
