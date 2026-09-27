using System.Windows;
using System.Windows.Input;

namespace Nightrunner.UI;

/// <summary>A one-line text prompt — WPF has no input box and rename needs one.</summary>
public partial class PromptWindow : Window
{
    private PromptWindow(string title, string prompt, string value)
    {
        InitializeComponent();
        Title = title;
        Prompt.Text = prompt;
        Input.Text = value;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    /// <summary>The typed text, or null when cancelled or left empty.</summary>
    public static string? Ask(DependencyObject? owner, string title, string prompt, string value = "")
    {
        var win = new PromptWindow(title, prompt, value) { Owner = GetWindow(owner) };
        if (win.ShowDialog() != true) return null;
        var text = win.Input.Text.Trim();
        return text.Length == 0 ? null : text;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
    }
}
