using System.Windows;

namespace AbsolverModTool.Gui;

public partial class CloneRowDialog : Window
{
    readonly HashSet<string> _existingKeys;

    public string SourceRowKey { get; private set; } = "";
    public string NewRowKey { get; private set; } = "";

    public CloneRowDialog(string assetName, IEnumerable<RowItem> rows)
    {
        InitializeComponent();
        PromptText.Text = $"Clone a row in {assetName}:";
        _existingKeys = rows.Select(r => r.Key).ToHashSet();

        foreach (var r in rows) SourceRowBox.Items.Add(r);
        if (SourceRowBox.Items.Count > 0) SourceRowBox.SelectedIndex = 0;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SourceRowBox.SelectedItem is not RowItem selected)
        {
            MessageBox.Show(this, "Choose a source row to clone.", "Clone Row");
            return;
        }
        var newKey = NewRowBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newKey))
        {
            MessageBox.Show(this, "Enter a new row key.", "Clone Row");
            return;
        }
        if (_existingKeys.Contains(newKey))
        {
            MessageBox.Show(this, $"Row '{newKey}' already exists - pick a different key.", "Clone Row");
            return;
        }

        SourceRowKey = selected.Key;
        NewRowKey = newKey;
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
