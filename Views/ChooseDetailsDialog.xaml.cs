using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace SweWolfFileExplorer.Views;

/// <summary>One row of the Choose Details dialog.</summary>
public class ColumnChoice : INotifyPropertyChanged
{
    private bool _isVisible;

    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public double Width { get; set; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>False for the Name column, which is always shown.</summary>
    public bool CanHide { get; init; } = true;
}

public partial class ChooseDetailsDialog : Window
{
    private const double MinColumnWidth = 20;

    private readonly ObservableCollection<ColumnChoice> _items;

    public IReadOnlyList<ColumnChoice> Result => _items;

    public ChooseDetailsDialog(IEnumerable<ColumnChoice> columns)
    {
        InitializeComponent();
        _items = new ObservableCollection<ColumnChoice>(columns);
        DetailsList.ItemsSource = _items;
        DetailsList.SelectedIndex = 0;
        Loaded += (_, _) => DetailsList.Focus();
    }

    private ColumnChoice? Selected => DetailsList.SelectedItem as ColumnChoice;

    private void UpdateButtons()
    {
        int i = DetailsList.SelectedIndex;
        var sel = Selected;
        MoveUpButton.IsEnabled   = sel != null && i > 0;
        MoveDownButton.IsEnabled = sel != null && i < _items.Count - 1;
        ShowButton.IsEnabled     = sel != null && !sel.IsVisible;
        HideButton.IsEnabled     = sel != null && sel.IsVisible && sel.CanHide;
        WidthBox.IsEnabled       = sel != null;
    }

    private void DetailsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void DetailsList_CheckChanged(object sender, RoutedEventArgs e) => UpdateButtons();

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        int i = DetailsList.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _items.Count) return;

        _items.Move(i, j);
        DetailsList.SelectedIndex = j;
        DetailsList.Focus();
    }

    private void Show_Click(object sender, RoutedEventArgs e) => SetVisible(true);

    private void Hide_Click(object sender, RoutedEventArgs e) => SetVisible(false);

    private void SetVisible(bool visible)
    {
        if (Selected is not { } sel) return;
        sel.IsVisible = visible;
        UpdateButtons();
        DetailsList.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        // Commit a width typed in the box when OK is clicked straight away.
        WidthBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        foreach (var item in _items)
            if (item.Width < MinColumnWidth) item.Width = MinColumnWidth;

        DialogResult = true;
    }
}
