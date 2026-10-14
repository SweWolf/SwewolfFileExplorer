# MyFileExplorer

A Windows File Explorer clone built with C# (.NET 10) and WPF.

## Build & Run

```bash
dotnet build
dotnet run
```

Output: `bin\Debug\net10.0-windows\MyFileExplorer.exe`

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

## Extending the App

- **New toolbar action**: add a `RelayCommand` property in `MainViewModel`, wire up `CanExecute` logic, then add a `<Button>` in `MainWindow.xaml` bound to the command.
- **New context menu item**: add a `<MenuItem>` in `MainWindow.xaml` with a `Click` handler, then call the relevant `MainViewModel` method from `MainWindow.xaml.cs`.
- **New converter**: add a class implementing `IValueConverter` in `Converters/`, register it as a resource in `MainWindow.xaml`, and use it in a `DisplayMemberBinding` or `CellTemplate`.
