# Ideas for future features

Collected 2026-10-28 while building the details block, the drives list and per-folder columns. Nothing here is decided or promised;
each item says what it would build on. `CLAUDE.md` has the finished features and their "Not done" lists.

## Details block (info panel, left of the preview picture)

Built: name in bold and rows by file type (`MainWindow.Details.cs`). Next steps:

- **Camera details of photos**: Date Taken, camera make and model, exposure, aperture, ISO, focal length. TagLibSharp can read them from
  JPEG and TIFF (no new package); HEIC and RAW would need own code (`GpsReader` already walks the same EXIF structures).
  Sort and filter by Date Taken would then be a natural follow-up (a column).
- **Folder details when nothing is selected**: number of items, total size (read in the background, with a limit for huge folders).
- **A Properties button** under the rows (Explorer has one).
- **Video title from the `.nfo`** when the file has no title inside it (the Title row now only shows the title stored in the video file;
  43 of 450 of the user's videos have one, often just the release name).
- More rows if wanted: Date Created for pictures and music, file attributes, the folder a bookmark points to, archive entry counts.

## Drives list on This PC

Built: Name, Type, File System, Total Size, Free Space. The user skipped the free-space bar and the "Devices and drives" heading.

- Sorting by clicking a header (few rows, so low priority).
- Network locations, Quick Access style virtual items (Explorer shows them there too).
- Ctrl + wheel font size and type-ahead on that grid; Alt+Enter and Ctrl+D.
- Free space refreshes only on F5 and when the window comes to the front; a slow timer could do it while This PC is shown.

## Columns and sort order per folder

Built: columns (shown and order) per folder, inherited from the nearest parent, "Use These Columns and Sort Order in All Subfolders".

- Remember column widths per folder too? (The user said no: they are fitted automatically.)
- A tree sort of subfolders that is inherited the same way (now only per folder).
- A setting or menu item to see where a folder's layout comes from (its own or the parent's).

## Bookmarks

- A Bookmarks menu in the menu bar, ordering of bookmarks (an ordering file), bookmarks inside zips.
- Names for path bookmarks (Ctrl+D does not ask).
- In the media player: a bookmark menu, naming a bookmark, bookmarks of a file inside an archive.

## Map symbol (GPS)

- A setting for which map site the left click uses (now Google Maps; the right-click menu has seven sites).
- GPX, KML and GeoJSON files (a track or many places, not one position).

## Where a file came from (Mark of the Web)

- Source URL and Source Page as library columns (see the memory note: the user decides later).
- Copy the library row when a file is copied (only a move carries it now).
- A menu item to open the Host URL and Referrer URL.

## Other open items from `CLAUDE.md` that came up

- Camera, AI and other readers: InvokeAI, NovelAI, Fooocus, more Automatic1111 settings than the seed.
- A real mouse / real key test of the many features that were only tested headless (the notes in `CLAUDE.md` say which).
