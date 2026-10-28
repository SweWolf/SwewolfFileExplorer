using System.Windows;
using System.Windows.Input;
using SwewolfFileExplorer.ViewModels;

namespace SwewolfFileExplorer;

/// <summary>
/// Dragging files out of the list to other programs (Chrome's upload boxes, Windows Explorer, mail, chat ...): the selected items go as an ordinary
/// file drop, so the other program decides what to do (copy, move or link; a program that only wants to read the file just reads it).
/// A file inside a compressed file is unpacked to the temporary folder first and the copy is dragged (copy only).
/// </summary>
public partial class MainWindow
{
    private Point _fileDragStart;
    private FileItemViewModel? _dragItem;          // the item the left button went down on (null = nothing to drag)
    private bool _dragWithRight;                   // the drag is made with the right button (the drop then asks what to do, as in Explorer)
    private FileItemViewModel? _selectOnRelease;   // a click on one of several selected items: it becomes the only one when the button comes up without a drag

    private void InitDragOut()
    {
        FileListView.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(DragOut_MouseDown), handledEventsToo: true);
        FileListView.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(DragOut_MouseMove), handledEventsToo: true);
        FileListView.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(DragOut_MouseUp), handledEventsToo: true);
        FileListView.AddHandler(UIElement.PreviewMouseRightButtonDownEvent, new MouseButtonEventHandler(DragOut_RightMouseDown), handledEventsToo: true);
        FileListView.AddHandler(UIElement.PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler((_, _) => { if (_dragWithRight) _dragItem = null; }), handledEventsToo: true);
    }

    /// <summary>The right button goes down on an item: a drag with it ends in a menu (Copy Here, Move Here, Create Shortcut Here). The selection and the right-click menu are left alone until it moves.</summary>
    private void DragOut_RightMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        _selectOnRelease = null;
        _dragWithRight = true;
        if (Helpers.AppSettingsStore.Current.DisableInternalDragDrop) return;   // Settings > Drag and Drop: no drag with the right button
        if (IsHeaderOrScrollBar(e.OriginalSource)) return;

        var item = FindItemUnder<FileItemViewModel>(e.OriginalSource);
        if (item == null) return;
        _fileDragStart = e.GetPosition(FileListView);
        _dragItem = item;
    }

    private void DragOut_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragWithRight = false;
        _dragItem = null;
        _selectOnRelease = null;
        if (e.ClickCount != 1 || IsHeaderOrScrollBar(e.OriginalSource)) return;

        var item = FindItemUnder<FileItemViewModel>(e.OriginalSource);
        if (item == null) return;

        _fileDragStart = e.GetPosition(FileListView);
        _dragItem = item;

        // A click on one of several selected items must not make it the only one yet: the user may be about to drag them all.
        bool plain = Keyboard.Modifiers == ModifierKeys.None;
        if (plain && FileListView.SelectedItems.Count > 1 && FileListView.SelectedItems.Contains(item))
        {
            _selectOnRelease = item;
            e.Handled = true;
            FileListView.Focus();
        }
    }

    private void DragOut_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null) return;
        var button = _dragWithRight ? e.RightButton : e.LeftButton;
        if (button != MouseButtonState.Pressed) { _dragItem = null; _selectOnRelease = null; return; }

        var now = e.GetPosition(FileListView);
        if (Math.Abs(now.X - _fileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _fileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var grabbed = _dragItem;
        _dragItem = null;
        _selectOnRelease = null;
        StartDragOut(grabbed);
    }

    private void DragOut_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var pending = _selectOnRelease;
        _dragItem = null;
        _selectOnRelease = null;
        if (pending == null) return;

        FileListView.SelectedItems.Clear();
        FileListView.SelectedItem = pending;
    }

    private void StartDragOut(FileItemViewModel grabbed)
    {
        // What is dragged: the selection if the grabbed item is part of it, else just the grabbed item (which becomes the selection).
        var items = FileListView.SelectedItems.OfType<FileItemViewModel>().ToList();
        if (!items.Contains(grabbed))
        {
            FileListView.SelectedItems.Clear();
            FileListView.SelectedItem = grabbed;
            items = new List<FileItemViewModel> { grabbed };
        }

        bool anyPacked = items.Any(i => i.IsInArchive);
        var paths = new List<string>();
        foreach (var item in items)
        {
            if (item.FullPath.Length == 0) continue;
            var path = FolderTabViewModel.RealPathOf(item);   // a packed file is unpacked first (a message says if it cannot be)
            if (path != null) paths.Add(path);
        }
        if (paths.Count == 0) return;

        var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
        // Copy and Move as Explorer offers them; a copy unpacked from a compressed file is only ever copied.
        var effects = anyPacked ? DragDropEffects.Copy : DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;

        DragDropEffects result;
        try { result = DragDrop.DoDragDrop(FileListView, data, effects); }
        catch (Exception) { return; }

        // A program that moved the files away: the list is read again (the folder watcher would notice too, a moment later).
        if (result.HasFlag(DragDropEffects.Move)) VM.RefreshCommand.Execute(null);
    }
}
