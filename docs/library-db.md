# Library database (schema version 1)

The app remembers which files were opened ("read") in SQLite files. A separate tool (for example a TeraTool importer)
may write into them, so the format is described here. Change it only with a new `PRAGMA user_version`.

## Where the files are

| Drive | File | Keys (`Folder`, `Path`) |
|---|---|---|
| Fixed drive, network share | `%AppData%\SweWolfSoftware\SweWolf File Explorer\library.db` | The full path without a trailing backslash, e.g. `D:\Photos\Trip` |
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
    PRIMARY KEY (Folder, Name)
) WITHOUT ROWID;
```

Only exceptions are stored: **a file without a row is unread**, a folder without a row has never been touched.

## Rules

- A file is read when it is opened in the app. Showing a thumbnail or unpacking an archive is not opening.
- A folder is **Read** when every visible file in it and every visible subfolder is read (`Complete = 1`). Hidden files and
  folders (the Hidden/System attribute, or an extension in Settings > Hidden Files) are not counted. A packed file counts
  as a subfolder. A folder that was never listed is not Read, so a parent turns Read only after its whole tree was seen
  (or after Mark as Read on it).
- A folder is **Partly Read** when a `File` row exists for it or below it: `Folder = key OR (Folder > key||'\' AND Folder < key||']')`
  (`]` is the character after the backslash). Otherwise it is Unread.
- To mark a whole tree as read from another tool, insert the `File` rows and set `Folder.OwnUnread = 0, Complete = 1`
  for every folder in it, then open the app (the parent folders are recalculated when they are next listed).

## Notes

- Journal mode is the default (rollback journal), not WAL, and the app never keeps a connection open: a removable drive
  must be safe to pull out and "Safely Remove" must not be blocked.
- Planned: resume position and length (the percent-watched column), rating, tag, cover choice, sort orders.
