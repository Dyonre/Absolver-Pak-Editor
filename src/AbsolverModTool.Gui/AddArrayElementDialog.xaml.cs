using System.Windows;

namespace AbsolverModTool.Gui;

record ArrayPropertyItem(string Name, int Count)
{
    public override string ToString() => $"{Name} ({Count} element(s))";
}

public partial class AddArrayElementDialog : Window
{
    public string ArrayProperty { get; private set; } = "";
    public string Value { get; private set; } = "";

    public AddArrayElementDialog(string assetName, IEnumerable<(string Name, int Count)> arrays)
    {
        InitializeComponent();
        PromptText.Text = $"Append a new element to a growable array in {assetName}:";

        foreach (var a in arrays) ArrayBox.Items.Add(new ArrayPropertyItem(a.Name, a.Count));
        if (ArrayBox.Items.Count > 0) ArrayBox.SelectedIndex = 0;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ArrayBox.SelectedItem is not ArrayPropertyItem selected)
        {
            MessageBox.Show(this, "Choose an array to append to.", "Add Array Slot");
            return;
        }
        var value = ValueBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            MessageBox.Show(this, "Enter a value for the new element.", "Add Array Slot");
            return;
        }

        ArrayProperty = selected.Name;
        Value = value;
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
