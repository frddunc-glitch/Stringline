using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Stringline.App.ViewModels;

namespace Stringline.App.Views;

/// <summary>
/// The timetable editor's window (<see cref="TimetableEditorViewModel"/>, S43). The code here only wires keys and the
/// grids' editing to the view model: Ctrl+S, Ctrl+Z and Ctrl+Y, F7; in the trip grid Insert, Ctrl+Delete and Ctrl+D,
/// and the code cells' drop-downs taking the key that started the edit. Closing with unsaved changes asks first.
/// </summary>
public partial class EditorWindow : Window
{
    private readonly TimetableEditorViewModel _viewModel;
    private bool _closeRequested;
    private bool _closing;

    internal EditorWindow(TimetableEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        viewModel.GoToRequested += OnGoToRequested;
        viewModel.IssuesRequested += OnIssuesRequested;
        InputBindings.Add(new KeyBinding(viewModel.SaveDraftCommand, Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(viewModel.UndoCommand, Key.Z, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(viewModel.RedoCommand, Key.Y, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(viewModel.RedoCommand, Key.Z, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(viewModel.CheckAllCommand, Key.F7, ModifierKeys.None));
        CommandBindings.Add(new CommandBinding(DeletePointsCommand, (_, _) => DeletePoints(), (_, e) => e.CanExecute = _viewModel.Train is not null));
        CommandBindings.Add(new CommandBinding(FillDownCommand, (_, _) => FillDown(), (_, e) => e.CanExecute = _viewModel.Train is not null));
        DetailsPanel.AddHandler(KeyDownEvent, new KeyEventHandler(OnDetailKeyDown), handledEventsToo: false);
        Loaded += (_, _) => TrainsGrid.Focus();
    }

    /// <summary>Edit ▸ Remove points (Ctrl+Delete in the trip grid).</summary>
    public static RoutedUICommand DeletePointsCommand { get; } = new("Remove points", nameof(DeletePointsCommand), typeof(EditorWindow));

    /// <summary>Edit ▸ Fill down (Ctrl+D in the trip grid).</summary>
    public static RoutedUICommand FillDownCommand { get; } = new("Fill down", nameof(FillDownCommand), typeof(EditorWindow));

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeRequested)
        {
            return;
        }

        // The user closed the window: the draft closes with it (asking first), and must not close the window again.
        _closing = true;
        try
        {
            e.Cancel = !_viewModel.RequestClose();
        }
        finally
        {
            _closing = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.GoToRequested -= OnGoToRequested;
        _viewModel.IssuesRequested -= OnIssuesRequested;
        base.OnClosed(e);
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        _closeRequested = true;
        if (!_closing)
        {
            Close();
        }
    }

    private void OnGoToRequested(object? sender, EventArgs e)
    {
        Tabs.SelectedItem = TrainTab;
        if (_viewModel.SelectedTrainRow is { } row)
        {
            TrainsGrid.ScrollIntoView(row);
        }

        if (_viewModel.Train?.CurrentTrip is { } trip)
        {
            Dispatcher.BeginInvoke(() =>
            {
                TripGrid.ScrollIntoView(trip);
                TripGrid.SelectedCells.Clear();
                if (TripGrid.Columns.Count > 1)
                {
                    TripGrid.CurrentCell = new DataGridCellInfo(trip, TripGrid.Columns[1]);
                    TripGrid.SelectedCells.Add(TripGrid.CurrentCell);
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void OnIssuesRequested(object? sender, EventArgs e) => IssuesGrid.Focus();

    private void OnTrainsSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.SelectedTrainRows = [.. TrainsGrid.SelectedItems.OfType<DraftTrainRow>()];

    private void OnTripCurrentCellChanged(object? sender, EventArgs e)
    {
        if (_viewModel.Train is { } train && TripGrid.CurrentCell.Item is TripRow row)
        {
            train.CurrentTrip = row;
        }
    }

    /// <summary>The rows with a selected cell (or the current row), in grid order.</summary>
    private List<TripRow> SelectedTrips()
    {
        var rows = TripGrid.SelectedCells.Select(c => c.Item).OfType<TripRow>().Distinct().OrderBy(r => r.Index).ToList();
        if (rows.Count == 0 && _viewModel.Train?.CurrentTrip is { } current)
        {
            rows.Add(current);
        }

        return rows;
    }

    private void DeletePoints()
    {
        if (_viewModel.Train is { } train)
        {
            train.DeletePoints(SelectedTrips());
        }
    }

    private void FillDown()
    {
        if (_viewModel.Train is { } train && TripGrid.CurrentCell.Column?.SortMemberPath is { Length: > 0 } cell)
        {
            train.FillDown(cell, SelectedTrips());
        }
    }

    private void OnTripGridKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.Train is not { } train || IsEditingCell(e.OriginalSource))
        {
            return;
        }

        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Insert when !control:
                train.InsertPointCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete when control:
                DeletePoints();
                e.Handled = true;
                break;
            case Key.D when control:
                FillDown();
                e.Handled = true;
                break;
        }
    }

    /// <summary>The key went to a cell being edited (its text box or drop-down), not the grid.</summary>
    private static bool IsEditingCell(object source) =>
        source is DependencyObject element && (element is TextBoxBase || element is ComboBox || FindParent<ComboBox>(element) is not null);

    /// <summary>A code cell's drop-down gets the focus, and the character that started the edit.</summary>
    private void OnTripCellPreparing(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (FindChild<ComboBox>(e.EditingElement) is not { } combo)
        {
            return;
        }

        combo.ApplyTemplate();
        if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox box)
        {
            box.Focus();
            if (e.EditingEventArgs is TextCompositionEventArgs typed)
            {
                box.Text = typed.Text;
            }

            box.CaretIndex = box.Text.Length;
        }
    }

    /// <summary>
    /// A committed cell is one edit at once (the grid would otherwise wait for the whole row); the code cells' drop-downs
    /// update on commit only, so Esc leaves the cell as it was.
    /// </summary>
    private void OnTripCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit)
        {
            return;
        }

        if (FindChild<ComboBox>(e.EditingElement) is { } combo)
        {
            BindingOperations.GetBindingExpression(combo, ComboBox.TextProperty)?.UpdateSource();
        }
        else if (e.EditingElement is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
    }

    /// <summary>Enter in one of the train's boxes makes its change at once, as leaving the box does.</summary>
    private void OnDetailKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    private static T? FindChild<T>(DependencyObject? parent)
        where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        if (parent is T self)
        {
            return self;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (FindChild<T>(VisualTreeHelper.GetChild(parent, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static T? FindParent<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (var e = VisualTreeHelper.GetParent(element); e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e is T found)
            {
                return found;
            }
        }

        return null;
    }
}
