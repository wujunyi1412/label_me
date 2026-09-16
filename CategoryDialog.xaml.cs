using System.Windows;

namespace LabelMeWpf;

public partial class CategoryDialog : Window
{
    public string? SelectedLabel { get; private set; }

    public CategoryDialog(IEnumerable<string> labels, string? currentLabel)
    {
        InitializeComponent();
        var items = labels.ToList();
        ExistingLabelCombo.ItemsSource = items;
        ExistingLabelCombo.SelectedItem = items.FirstOrDefault(x => x == currentLabel) ?? items.FirstOrDefault();
        Loaded += (_, _) => ExistingLabelCombo.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var newLabel = NewLabelTextBox.Text.Trim();
        SelectedLabel = !string.IsNullOrWhiteSpace(newLabel)
            ? newLabel
            : ExistingLabelCombo.SelectedItem as string;

        if (string.IsNullOrWhiteSpace(SelectedLabel))
        {
            MessageBox.Show(this, "请选择已有类别或输入新类别。", "缺少类别", MessageBoxButton.OK, MessageBoxImage.Information);
            NewLabelTextBox.Focus();
            return;
        }
        DialogResult = true;
    }

    private void ExistingLabelCombo_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ExistingLabelCombo.SelectedItem == null) return;
        NewLabelTextBox.Clear();
        Confirm_Click(sender, e);
        e.Handled = true;
    }
}
