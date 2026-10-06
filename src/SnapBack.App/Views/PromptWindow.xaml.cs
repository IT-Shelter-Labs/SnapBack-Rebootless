using System.Windows;

namespace SnapBack.App.Views;

/// <summary>Asks for a short text, offering editable suggestions.</summary>
public partial class PromptWindow : Window
{
    public PromptWindow(string title, string message, string initial, IReadOnlyList<string> suggestions)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ValueBox.ItemsSource = suggestions;
        ValueBox.Text = initial;
        Loaded += (_, _) => ValueBox.Focus();
    }

    public string Value => ValueBox.Text.Trim();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Value.Length > 0)
        {
            DialogResult = true;
        }
    }
}
