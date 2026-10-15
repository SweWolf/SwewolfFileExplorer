# MyFileExplorer

A Windows File Explorer clone built with C# (.NET 10) and WPF.

## Build & Run

```bash
dotnet build
dotnet run
```

Output: `bin\x64\Debug\net10.0-windows\MyFileExplorer.exe` (rebuild it after every change, so the user can run it without Visual Studio)

## Project Structure

```
MyFileExplorer/
├── Commands/
│   └── RelayCommand.cs         ICommand implementation with RaiseCanExecuteChanged
├── Converters/
│   └── FileSizeConverter.cs    IValueConverter: bytes → human-readable string
├── Helpers/
│   └── IconHelper.cs           Windows Shell icons via SHGetFileInfo (P/Invoke)
├── ViewModels/
│   ├── MainViewModel.cs        All application logic (MVVM)
│   ├── FileItemViewModel.cs    Represents a file/folder row in the list
│   └── TreeItemViewModel.cs    Represents a node in the navigation tree (lazy-loaded)
├── Views/
│   └── InputDialog.xaml(.cs)   Generic single-input dialog (used for Rename)
├── App.xaml(.cs)
└── MainWindow.xaml(.cs)        Main window — UI only, delegates to MainViewModel
```

## Architecture

- Pattern: **MVVM**. `MainWindow.xaml.cs` handles UI events only (double-click, F2, context menu clicks) and delegates all logic to `MainViewModel`.
- No external NuGet packages — only .NET 10 BCL and WPF.
- Target platform: **Windows x64** (`net10.0-windows`).

## Key Design Decisions

- **Async icon loading**: `FileItemViewModel.LoadIconAsync()` fetches shell icons on a background thread and marshals back to the UI dispatcher. This keeps the list responsive when navigating large folders.
- **Lazy tree loading**: `TreeItemViewModel` adds a dummy child on init so the expand arrow appears. On first expand, `LoadChildren()` replaces the dummy with real subdirectories.
- **Clipboard state** is stored in `MainViewModel` (`_clipboardPaths`, `_clipboardIsCut`) — not the system clipboard — so it is local to the app.
- **Column layout**: right-click a column header for a checklist of columns and "More..." (Choose Details dialog: order, show/hide, width). Hidden columns are kept in `MainWindow`'s `_layout` list and removed from the GridView. The layout is saved on every change and on close in `%AppData%\SweWolfSoftware\MyFileExplorer\columns.json` (`ColumnLayoutStore`).
- **Tree follows the folder**: every navigation (list, address bar, back/up) expands the tab's tree to the folder and selects its node (`SelectInTree`). A node selected this way is remembered in `_programmaticSelection`, so the window does not treat it as a click and navigate again.
- **Folders in the list**: hidden by default (they are in the tree); the "Folders" toggle button in the toolbar shows them. `SharedState.ShowFolders`, saved in `settings.json` (`AppSettingsStore`) next to `columns.json`. `FolderTabViewModel.Refresh()` also re-reads the current folder's tree node, so new, renamed or deleted folders show in the tree.
- **Font size**: Ctrl + mouse wheel over the list or over the tree changes that control's font size (8-40 pt, default 13), separately. The new size is flashed in the title bar for 2.5 s and saved in `settings.json` (`ListFontSize`, `TreeFontSize`). Always save through `SharedState.Settings`, so one setting never overwrites another.
- **Navigation history**: two `Stack<string>` fields (`_backHistory`, `_forwardHistory`) inside `MainViewModel`.

## Keyboard Shortcuts

| Key | Action |
|---|---|
| Enter / Double-click | Open file or navigate into folder |
| Backspace | Go up one level |
| Alt + ← / → | Back / Forward |
| F2 | Rename selected item |
| F5 | Refresh |
| Delete | Delete selected item(s) |
| Ctrl+C / X / V | Copy / Cut / Paste |
| Ctrl+Shift+N | New folder |
| Ctrl+T or Ctrl+N | New tab |
| Ctrl+W or Ctrl+F4 | Close tab |
| Ctrl+Shift+T | Reopen the last closed tab (up to 20, with its history and sort) |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / Previous tab |
| Middle-click | On a tab: close it. On a folder: open it in a new tab |
| Ctrl+Num + | Size all columns to fit their widest text |
| Alt+Enter | Properties of the selected item(s), or of the current folder |

## Extending the App

- **New toolbar action**: add a `RelayCommand` property in `MainViewModel`, wire up `CanExecute` logic, then add a `<Button>` in `MainWindow.xaml` bound to the command.
- **New context menu item**: add a `<MenuItem>` in `MainWindow.xaml` with a `Click` handler, then call the relevant `MainViewModel` method from `MainWindow.xaml.cs`.
- **New converter**: add a class implementing `IValueConverter` in `Converters/`, register it as a resource in `MainWindow.xaml`, and use it in a `DisplayMemberBinding` or `CellTemplate`.
