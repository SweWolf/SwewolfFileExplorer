# SweWolf File Explorer (SweWolfFileExplorer)

A Windows File Explorer clone built with C# (.NET 10) and WPF.

## Build & Run

```bash
dotnet build
dotnet run
```

Output: `bin\x64\Debug\net10.0-windows\SweWolfFileExplorer.exe` (rebuild it after every change, so the user can run it without Visual Studio)

## Project Structure

```
SweWolfFileExplorer/
├── Commands/
│   └── RelayCommand.cs         ICommand implementation with RaiseCanExecuteChanged
├── Converters/
│   └── FileSizeConverter.cs    IValueConverter: bytes → human-readable string
├── Helpers/
│   ├── IconHelper.cs           Windows Shell icons via SHGetFileInfo (P/Invoke)
│   ├── AppSettingsStore.cs     settings.json (AppSettings, one shared instance)
│   ├── ThemeService.cs         Light / Dark / System colors
│   ├── AppInfo.cs              App display name (window titles, message box captions)
│   ├── ThumbnailLoader.cs      Small lock-free preview of a picture; also says which files are pictures
│   ├── ArchiveSupport.cs       Archives as folders (zip, RAR, 7z: browse in place, nested archives, unpack to copy or open)
│   ├── SevenZipRunner.cs       Runs 7-Zip's 7z.exe to list and read RAR / 7z archives
│   ├── PictureLoader.cs        Picture for the viewer: EXIF orientation, animated GIF frames
│   ├── GifAnimator.cs          Puts GIF frames together (disposal methods), looping
│   └── GitHubUpdateChecker.cs  Asks GitHub Releases for a newer version (copy of MP3toMP4's, has a History list)
├── ViewModels/
│   ├── MainViewModel.cs        All application logic (MVVM)
│   ├── FileItemViewModel.cs    Represents a file/folder row in the list
│   └── TreeItemViewModel.cs    Represents a node in the navigation tree (lazy-loaded)
├── Views/
│   ├── InputDialog.xaml(.cs)   Generic single-input dialog (used for Rename)
│   ├── SettingsWindow.xaml(.cs)        Tools > Settings (Appearance: Style)
│   ├── CreateShortcutWindow.xaml(.cs)  Tools > Create Shortcut...
│   ├── PictureViewerWindow.xaml(.cs)   Full-screen picture viewer
│   └── AboutWindow.xaml(.cs)           Help > About
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
- **Column layout**: right-click a column header for a checklist of columns and "More..." (Choose Details dialog: order, show/hide, width). Hidden columns are kept in `MainWindow`'s `_layout` list and removed from the GridView. The layout is saved on every change and on close in `%AppData%\SweWolfSoftware\SweWolf File Explorer\columns.json` (`ColumnLayoutStore`).
- **Tree follows the folder**: every navigation (list, address bar, back/up) expands the tab's tree to the folder and selects its node (`SelectInTree`). A node selected this way is remembered in `_programmaticSelection`, so the window does not treat it as a click and navigate again.
- **Folders in the list**: hidden by default (they are in the tree); the "Folders" toggle button in the toolbar shows them. `SharedState.ShowFolders`, saved in `settings.json` (`AppSettingsStore`) next to `columns.json`. `FolderTabViewModel.Refresh()` also re-reads the current folder's tree node, so new, renamed or deleted folders show in the tree.
- **Font size**: Ctrl + mouse wheel over the list or over the tree changes that control's font size (8-40 pt, default 13), separately. The new size is flashed in the title bar for 2.5 s and saved in `settings.json` (`ListFontSize`, `TreeFontSize`). Always save through the one settings object, `AppSettingsStore.Current` (`SharedState.Settings` is the same instance), so one setting never overwrites another.
- **Name**: the app is called "SweWolf File Explorer" in the UI (`AppInfo.Name`, main window title); "My File Explorer" was a beta name. The settings folder is `%AppData%\SweWolfSoftware\SweWolf File Explorer` (`AppInfo.Name`; settings of the beta name were not moved). The EXE, project, namespace and repository are `SweWolfFileExplorer` (renamed from MyFileExplorer before any release).
- **About**: **Help > About** (`Views/AboutWindow`, as in the siblings: blue banner with icon, version from the assembly and "latest version" check against GitHub Releases `SweWolf/SweWolfFileExplorer`, silent when offline or when there is no release yet). The version is `<Version>` in the .csproj. Help has no "SweWolf File Explorer Help" item yet (no help file).
- **Menu, Settings, Create Shortcut**: the menu bar has **Tools > Create Shortcut...** (desktop / Start menu, current or all users; `Views/CreateShortcutWindow`, as in the sibling apps, but without "Send To" until the app accepts a folder on the command line) and **Tools > Settings** (`Views/SettingsWindow`: Appearance > Style: Light / Dark / System, default System, saved as `Style` in `settings.json`). Message boxes of these windows use `AppInfo.Name` as caption (older ones still say "Error").
- **Cyber style** (working name; the user will rename it): the Dark theme with black backgrounds and light blue text (#ADD8E6, the colors of the old TeraTool). `ThemeService.ApplyCyberColors` replaces the theme's own white-text and window-background brushes by name in `Application.Resources` and removes them again when another style is chosen.
- **Hidden items**: files and folders with the Hidden or System attribute are not listed. A line under the list says "N hidden items (click to show)" and toggles them for the current folder (`FolderTabViewModel._showHidden`, reset on every navigation, kept on refresh); shown ones are faded (`FileItemViewModel.IsHidden`).
- **Archives (.zip, .cbz read by .NET; .rar, .cbr, .7z read by 7-Zip)** (`Helpers/ArchiveSupport`; `ArchiveReader` has one implementation per way of reading: `ZipReader` and `SevenZipReader`; `Helpers/SevenZipRunner` runs `7z.exe`: `l -slt -ba` lists, `e -so -spd` writes one file to the output, so RAR/7z are read in place too, like zip; 7-Zip is found through the registry (`HKLM`/`HKCU` `SOFTWARE\7-Zip`), Program Files, then the PATH; without it entering a RAR shows "7-Zip is needed ..." in the status bar; every call passes a dummy `-p` password, so an archive that needs a password fails at once with a clear message instead of 7-Zip waiting for input; an archive inside another one: a zip is read into memory, anything else is unpacked once to `%TEMP%\SweWolfFileExplorer\<pid>\nested\`; any mix works: zip in RAR, RAR in zip ...; a RAR that stores no date shows the date of the archive file; a **solid** RAR is slow for one picture after another, as 7-Zip must decode what comes before it). A packed file is a folder. In the tree the archives come after all the folders, with their extension (`TreeItemViewModel.IsArchive`; always an expand arrow, as looking inside costs a read); Enter / double-click on one in the list enters it; the address bar takes `D:\Photos\Trip.zip\Day 1`, and a zip inside a zip works (`...\All.zip\Trip.zip\Day 1`). They are read **in place** (`System.IO.Compression`, nothing is unpacked to browse); an index per archive is cached (`GetIndex`, invalidated by F5 / `Refresh`). Every path in the app can be such a path: `FileItemViewModel.IsInArchive` makes the icon and type come from the name (`IconHelper.GetIconForName`, cached per extension); `ArchiveSupport.OpenAnyFile` is what `PictureLoader` / `ThumbnailLoader` read through, so the viewer and the thumbnail work on pictures inside archives. A file opened from an archive is unpacked to `%TEMP%\SweWolfFileExplorer\<pid>\<guid>\` first (`FolderTabViewModel.RealPathOf`; removed at exit, stale ones at start). **Read-only:** inside an archive Cut, Delete, Rename, New Folder, Paste, Send to and Properties are disabled (`FolderTabViewModel.InArchive`); Copy then Paste into a real folder unpacks (files, folders, a zip inside a zip as the file). **Sort inside an archive:** by default none (`SortColumn.None`, the view has no `CustomSort`): the files show in the order they are stored in the zip, with no arrow in a header, as in TeraTool; a click on a header sorts (and is remembered for that folder like any folder's sort). Real folders still default to Name. The same for the tree: the folders (then the zips) inside an archive show in stored order until a sort is chosen in the tree's Sort By box, which is then empty (`SharedState.GetTreeSort` gives `None`, `TreeSortIndex` -1); choosing a column or a direction there sorts them. There is no way back to "stored order" except opening the archive again in a new run (the choice is not saved yet). Names without the UTF-8 flag (older tools, Windows' own "Send to compressed folder") are read in the computer's DOS code page (`NameEncoding`), as Explorer does: .NET would read them as UTF-8 and show "�" for å, ä, ö. Dates are the DOS time stored in the zip (the wall clock of the computer that zipped it); Explorer may differ by hours because it uses the UTC timestamp some zips also carry. The path of a zip inside a zip is both the file and the top of its archive: `Locate(..., asEntry)` picks. Not done yet: tar / gz / bz2 / xz and the other formats 7-Zip knows (only `ArchiveSupport`'s extension lists and `FilePatterns` need them), passwords (a message says it is protected; asking for the password is a later step), multi-volume RAR (`.part1.rar`), a setting for where 7-Zip is, a progress bar for very big archives (the tree reads the archive on the UI thread when expanded).
- **Picture viewer** (`Views/PictureViewerWindow`, modeled on TeraTool's `frmPicture`; spec and source are read-only references): Enter, double-click and Open on a picture (`ThumbnailLoader.IsImage`) open it here instead of in the program Windows has for it; **Open With...** in the context menu opens the Windows "Open with" dialog. The viewer starts full screen on black (covers the taskbar; `WindowStyle.None` + `NoResize` + `Maximized`), F11 / F / double-click toggle a window with a title bar that is still maximized (the taskbar shows again). It goes through the pictures of the list as sorted (`PictureChanged` makes the list select the current one; after Delete the list is refreshed). Keys: Left/Right, PageUp/PageDown, Backspace/Space/Enter/N/P, Home/End, wheel (down = next), click (left third = previous, else next, delayed by the double-click time so a double click is not also a click); Esc/X closes; numpad +/- or Ctrl + wheel zoom (about the mouse), numpad * = Stretch (fit, may enlarge), numpad / = Normal Size (fit, never enlarges); drag pans when zoomed; Ctrl+K / Ctrl+L rotate **for viewing only: the viewer never writes a file** (decision of the user, to be revisited); Delete asks, then moves the file to the Recycle Bin. `PictureLoader` reads the file into memory (never locked), applies EXIF orientation, and animates GIFs (`GifAnimator`). The picture lives in a `Canvas` and is placed only by its `RenderTransform` (`UpdateTransform`): in a Grid a picture bigger than the window is squeezed to the window's size and then scaled again (a 6016x4000 photo showed small and distorted). Blocks the screensaver and screen sleep while open. Pictures inside an archive work (Delete says it cannot delete from an archive). Not done yet (on purpose): slideshow, starting the app with a file path.
- **Info panel under the list**: "File 14/17" (place of the selected item in the list as sorted, computed in `MainWindow.UpdateInfoPanel`) and a thumbnail of the selected picture. `ThumbnailLoader` reads the file with `FileShare.ReadWrite | Delete`, decodes it into memory (`OnLoad`) and closes it, on a background thread, so the preview never locks the file. (The thumbnail does not apply EXIF rotation yet; the viewer does.)
- **Dark mode**: `ThemeService.Apply` sets `Application.ThemeMode` (Fluent Light or Dark; experimental API, WPF0001 is silenced in the .csproj) and the app's own brushes (`PaneBrush`, `LineBrush`, `TabHoverBrush`, `TabSelectedBrush`, `SplitterBrush`), which the XAML uses with `DynamicResource`: never hard-code a color in XAML. Applied before the main window opens (`App.OnStartup`) and again on a change in Settings; "System" follows Windows live. Switching back to the classic theme (`ThemeMode.None`) at runtime leaves wrong colors, so don't use it. Fluent makes the list rows taller than the old classic look; an implicit `ListViewItem` style based on the theme's breaks the rows, so it stays as it is.
- **Tree header**: above the tree: an up button (`UpCommand`), "Sort By" (Name / Date Created / Date Modified, Asc / Desc) and an "x" (flat, red under the mouse like a Windows close button: `CloseHoverBrush`, `ClosePressedBrush`) that hides the tree pane (F8 or the "Tree" toggle in the toolbar shows it again; `MainViewModel.ShowTree`, `MainWindow.UpdateTreePane`, not saved). The sort is per folder and sorts that folder's subfolders: `SharedState.TreeSorts` (in memory only, like `FolderSorts`: to be saved later in the DB file). `TreeItemViewModel` gets the lookup (`TreeSortLookup`) and `Resort()` moves the loaded nodes in place. Modeled on TeraTool (spec: `G:\My Drive\TeraTool_spec\`, source: `G:\My Drive\TT-SourceCode`, read-only).
- **Window size**: the main window always opens maximized (`WindowState="Maximized"`; Height/Width are the size when restored). The tree pane starts 20 % of the screen's work area wide (`TreeWidthFraction` in `MainWindow.xaml.cs`; a temporary choice: the user will decide later how its width should be set; the 308 in the XAML is only a placeholder that the code replaces).
- **Navigation history**: two `Stack<string>` fields (`_backHistory`, `_forwardHistory`) inside `MainViewModel`.

## Keyboard Shortcuts

| Key | Action |
|---|---|
| Enter / Double-click | Open file or navigate into folder |
| Backspace | Go up one level |
| Alt + ← / → | Back / Forward |
| F2 | Rename selected item |
| F5 | Refresh |
| F8 | Show / hide the folder tree |
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
| (in the picture viewer) | Left/Right previous/next, F11 full screen, Esc close, Delete, Ctrl+K / Ctrl+L rotate, numpad + - * / zoom: see "Picture viewer" above |

## Extending the App

- **New toolbar action**: add a `RelayCommand` property in `MainViewModel`, wire up `CanExecute` logic, then add a `<Button>` in `MainWindow.xaml` bound to the command.
- **New context menu item**: add a `<MenuItem>` in `MainWindow.xaml` with a `Click` handler, then call the relevant `MainViewModel` method from `MainWindow.xaml.cs`.
- **New converter**: add a class implementing `IValueConverter` in `Converters/`, register it as a resource in `MainWindow.xaml`, and use it in a `DisplayMemberBinding` or `CellTemplate`.
