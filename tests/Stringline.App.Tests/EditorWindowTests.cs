using System.Windows;
using System.Windows.Controls;
using Stringline.Core;
using System.Windows.Input;
using System.Windows.Media;
using Stringline.App.ViewModels;
using Stringline.App.Views;
using Stringline.Gateway.Tests;
using Stringline.Timetable.Editing;

namespace Stringline.App.Tests;

/// <summary>
/// The timetable editor's window (S43) over a Waterloo draft: it lays out with no binding errors, its menus have
/// unique access keys and every item a command, its shortcuts are bound, and typing in the trip grid's cells (a text
/// cell and a code cell's drop-down) edits the draft.
/// </summary>
public sealed class EditorWindowTests
{
    private const string Waterloo = "waterloo 2010.WTT";

    private static T Find<T>(DependencyObject parent, Func<T, bool>? match = null)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found && (match is null || match(found)))
            {
                return found;
            }

            if (FindOrNull(child, match) is { } deeper)
            {
                return deeper;
            }
        }

        throw new InvalidOperationException($"No {typeof(T).Name} found.");
    }

    private static T? FindOrNull<T>(DependencyObject parent, Func<T, bool>? match)
        where T : DependencyObject
    {
        try
        {
            return Find(parent, match);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    [Fact]
    public async Task The_editor_window_lays_out_its_menus_are_bound_and_typing_in_the_trip_grid_edits_the_draft()
    {
        await using var app = new TestApp(s => s with { TimetableFolder = Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid()) }, wpf: true);
        var vm = app.ViewModel;
        await app.Ui.Run(() => vm.OpenNewDraft(Fixtures.Path(Waterloo)));
        var draft = await app.Ui.Run(() => vm.Draft!);
        await app.Ui.WaitUntil(() => draft.KnowledgeSummary.StartsWith("No other", StringComparison.Ordinal), "the knowledge (none)");

        var errors = new List<string>();
        await app.Ui.Run(() =>
        {
            using var listener = new ProfileEditorWindowTests.BindingErrorListener(errors);
            var window = new EditorWindow(draft) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            window.Show();
            try
            {
                draft.SelectedTrainRow = draft.Trains.First(r => r.Headcode == "1W51");
                MainWindowTests.Settle(window);
                window.TrainsGrid.Items.Count.Should().Be(draft.Trains.Count);
                window.TripGrid.Items.Count.Should().Be(draft.Train!.Trips.Count);

                // Menus: unique access keys, every item bound, every shortcut a key binding (the grid's own keys aside).
                var problems = new List<string>();
                var gestures = window.InputBindings.OfType<KeyBinding>()
                    .Select(b => new KeyGestureConverter().ConvertToInvariantString(b.Gesture)).ToHashSet();
                foreach (var menu in window.EditorMenu.Items.OfType<MenuItem>().Prepend(null))
                {
                    var items = (menu is null ? window.EditorMenu.Items : menu.Items).OfType<MenuItem>().ToList();
                    var keys = items.Select(i => MenuText.AccessKey((string)i.Header)).ToList();
                    if (keys.Any(k => k is null) || keys.Distinct().Count() != keys.Count)
                    {
                        problems.Add($"{menu?.Header ?? "bar"}: access keys {string.Join(", ", keys)}");
                    }

                    foreach (var item in items.Where(i => menu is not null && !i.HasItems))
                    {
                        if (item.Command is null)
                        {
                            problems.Add($"{item.Header}: no command");
                        }

                        if (item.InputGestureText is { Length: > 0 } shown && shown is not ("Insert" or "Ctrl+Delete" or "Ctrl+D") && !gestures.Contains(shown))
                        {
                            problems.Add($"{item.Header} ({shown}): no key binding");
                        }
                    }
                }

                problems.Should().BeEmpty();

                // A text cell: the departure, typed as 1234H.
                var grid = window.TripGrid;
                var row = draft.Train.Trips[0];
                var departure = new TripNode(draft.Document, row.Element).DepPassTime!.Value;
                var departureColumn = grid.Columns.Single(c => c.SortMemberPath == nameof(TripRow.Departure));
                grid.CurrentCell = new DataGridCellInfo(row, departureColumn);
                grid.BeginEdit();
                MainWindowTests.Settle(window);
                var box = (TextBox)departureColumn.GetCellContent(row);
                box.Text = SimTimeNotation.Format(departure, compact: true) + "H";
                grid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
                MainWindowTests.Settle(window);
                new TripNode(draft.Document, row.Element).DepPassTime.Should().Be(departure + 30, draft.Error);

                // A code cell: the platform, through its drop-down.
                var platformColumn = grid.Columns.Single(c => c.SortMemberPath == nameof(TripRow.Platform));
                grid.CurrentCell = new DataGridCellInfo(row, platformColumn);
                grid.BeginEdit();
                MainWindowTests.Settle(window);
                var combo = Find<ComboBox>(platformColumn.GetCellContent(row), c => c.IsEditable);
                combo.Text = "7";
                grid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
                MainWindowTests.Settle(window);
                new TripNode(draft.Document, row.Element).Platform.Should().Be("7");
                draft.UndoHeader.Should().Contain("platform 7");

                window.Tabs.SelectedIndex = 1;
                MainWindowTests.Settle(window);
            }
            finally
            {
                draft.Editor.Undo();
                draft.Editor.Undo();
                window.Close();
            }
        });

        errors.Should().BeEmpty();
        (await app.Ui.Run(() => vm.Draft)).Should().BeNull("closing the window closes the draft");
    }
}
