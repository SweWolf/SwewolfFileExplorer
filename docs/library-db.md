# Library database (schema version 4)

SweWolf File Explorer and SweWolf Media Player remember which files were opened ("read") and where a video stopped
(resume position) in SQLite files. Both apps use the same files, and a separate tool (for example a TeraTool importer)
may write into them, so the format is described here. This copy (in the explorer) is the master.
Change the schema only by **adding** (new columns, a higher `PRAGMA user_version`), so an older app still works.
The code that finds the file and writes the rows is `Helpers/LibraryStore.cs`: it is copied between the apps as it is.

## Where the files are

| Drive | File | Keys (`Folder`, `Path`) |
|---|---|---|
| Fixed drive, network share | `%AppData%\SweWolfSoftware\Library\library.db` (shared by the apps; version 1 of the explorer kept it in `...\SweWolf File Explorer\`, and the file is moved once) | The full path without a trailing backslash, e.g. `D:\Photos\Trip` |
| Removable drive (Windows says removable, or the disk hangs on a USB / FireWire / SD / MMC bus: an external USB hard disk is reported as a fixed disk but is found this way) | `E:\.SweWolfFileExplorerData\library.db` (the folder has the Hidden attribute; created the first time a file is opened, never by browsing) | The path from the drive's root, starting with a backslash, e.g. `\Photos\Trip`. The root itself is the empty string |
| CD/DVD, or a removable drive that cannot be written | The main file | `vol:` + the volume serial number in 8 hex digits + the path from the root, e.g. `vol:B847843C\Photos\Trip` |

Paths inside a packed file continue the path of the file: `\Photos\Trip.zip\Day 1`.

## Tables

```sql
CREATE TABLE Folder (
    Path      TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
    OwnUnread INTEGER,                 -- visible files not read, as of the last time the folder was listed
    Complete  INTEGER NOT NULL DEFAULT 0   -- 1: every visible file and every subfolder is read
) WITHOUT ROWID;

CREATE TABLE File (
    Folder TEXT NOT NULL COLLATE NOCASE,   -- the key of the folder
    Name   TEXT NOT NULL COLLATE NOCASE,   -- the file name
    ReadAt TEXT,                            -- UTC, ISO 8601: when it was first read
    Position INTEGER,                       -- schema 2: where the user stopped, in milliseconds (NULL: nothing saved)
    Length   INTEGER,                       -- schema 2: the length of the video or song, in milliseconds
    PRIMARY KEY (Folder, Name)
) WITHOUT ROWID;

-- schema 3: the user's own rating and expectation of a file or a folder (a table of its own, so that a File row keeps
-- meaning "opened": marking a file unread must not remove its rating, and an older app knows nothing of this table)
CREATE TABLE Rated (
    Folder      TEXT NOT NULL COLLATE NOCASE,   -- the key of the folder that holds the item (a drive's root: the empty string)
    Name        TEXT NOT NULL COLLATE NOCASE,   -- the name of the file or folder (a drive's root: its own key)
    Rating      INTEGER,                        -- 1..10, whole number; NULL = none
    Expectation REAL,                           -- 1..10, decimals allowed (how good the user expects it to be); NULL = none
    PRIMARY KEY (Folder, Name)
) WITHOUT ROWID;

-- schema 4: what the user chose for a folder (only for folders where they chose something)
CREATE TABLE FolderPrefs (
    Path              TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,   -- the key of the folder (like Folder.Path)
    SortColumn        TEXT,       -- how the list of the folder is sorted: a name of the explorer's SortColumn (Name, DateModified, DateCreated, Type, Size, Artist, Title, TrackNumber, Rating, Expectation, None)
    SortAscending     INTEGER,    -- 1 ascending, 0 descending
    TreeSortColumn    TEXT,       -- how the subfolders of the folder are sorted in the tree (Name, DateCreated, DateModified)
    TreeSortAscending INTEGER
) WITHOUT ROWID;
```

Only exceptions are stored: **a file without a row is unread**, a folder without a row has never been touched.

## Rules

- A file is read when it is opened in the app. Showing a thumbnail or unpacking an archive is not opening. In the
  media player a video or song counts as read after it has been played for 60 seconds (or to its end).
- A saved position belongs to a read file: saving one creates the row. When the video is played to its end the position is
  cleared but the row (read) stays. Marking the file unread deletes the row, position included.
- A folder is **Read** when every visible file in it and every visible subfolder is read (`Complete = 1`). Hidden files and
  folders (the Hidden/System attribute, or an extension in Settings > Hidden Files) are not counted. A packed file counts
  as a subfolder. A folder that was never listed is not Read, so a parent turns Read only after its whole tree was seen
  (or after Mark as Read on it).
- A folder is **Partly Read** when a `File` row exists for it or below it: `Folder = key OR (Folder > key||'\' AND Folder < key||']')`
  (`]` is the character after the backslash). Otherwise it is Unread.
- To mark a whole tree as read from another tool, insert the `File` rows and set `Folder.OwnUnread = 0, Complete = 1`
  for every folder in it, then open the app (the parent folders are recalculated when they are next listed).

## Damage and backups

- **A damaged file** is found by SQLite's `quick_check` when an app opens it (once per file and run), or when a call fails with
  "malformed" or "not a database". It is moved aside as `library.corrupt-<date>.db` in the same folder (nothing is deleted)
  and replaced: the main file by the newest good backup, a USB file or a volume file by a new, empty one. The app shows a
  message with the new name of the old file. To try to rescue data from it, open it in an SQLite tool (the `sqlite3` command
  `.recover` can read much of a broken file).
- **Backups of the main file:** the first start of an app each day makes `Backups\library-yyyyMMdd.db` next to
  `library.db` (SQLite's backup, so it is consistent while another app writes). The last 7 are kept. The USB files are not
  backed up: they travel with the drive. To restore by hand: close both apps and copy a backup over `library.db`.

## Notes

- Journal mode is the default (rollback journal), not WAL, and the app never keeps a connection open: a removable drive
  must be safe to pull out and "Safely Remove" must not be blocked.
- Both apps may run at once: each opens the file only for a moment and waits up to 5 seconds when it is busy. The explorer
  reads the read marks when it lists a folder, so something the player marked shows after F5 or when the window is activated.
  A folder's stored state (`Folder` table) is only recalculated by the explorer, when the folder is listed.
- **Delete, rename, move:** the explorer removes the rows of a deleted file or folder (and everything below a folder) and moves
  them to the new key when it renames or moves something itself (`LibraryStore.Forget` / `Move`: `File`, `Rated` and `Folder` rows,
  the rows below a folder are found with the key range `K\` .. `K]`). Things changed outside the app leave their rows behind.
- **Rating and expectation** (schema 3): set with Ctrl+R / Ctrl+E on files and folders (also inside packed files, which are keyed like
  everything else). A row with neither value is deleted. Sorting treats no value as 0. A rename or move done in the explorer carries them
  along with the read marks.
- **Sort order** (schema 4): saved when the user clicks a column header in the list or chooses the Sort By order of the tree;
  read the first time a folder is shown in a run. `Forget` and `Move` carry the `FolderPrefs` rows with the folder.
- Planned: the percent-watched column (position divided by length), tag, cover choice.
