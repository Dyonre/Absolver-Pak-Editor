using System.Windows;
using System.Windows.Controls;

namespace AbsolverModTool.Gui;

public partial class InputDialog : Window
{
    readonly List<TextBox> _boxes = new();

    public string Prompt { get; }
    public string[] Values { get; private set; } = Array.Empty<string>();

    /// <param name="fieldLabels">One label per text field requested.</param>
    public InputDialog(string title, string prompt, params string[] fieldLabels)
    {
        Title = title;
        Prompt = prompt;
        InitializeComponent();
        DataContext = this;

        foreach (var label in fieldLabels)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(new TextBlock { Text = label });
            var box = new TextBox { Margin = new Thickness(0, 2, 0, 0) };
            _boxes.Add(box);
            panel.Children.Add(box);
            ((Panel)FieldsHost.Parent).Children.Insert(((Panel)FieldsHost.Parent).Children.IndexOf(FieldsHost), panel);
        }
        FieldsHost.Visibility = Visibility.Collapsed;
        if (_boxes.Count > 0) _boxes[0].Focus();
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        Values = _boxes.Select(b => b.Text.Trim()).ToArray();
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
