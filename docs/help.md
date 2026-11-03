# SweWolf File Explorer Help

SweWolf File Explorer is a file manager for Windows. It looks and works much like Windows
Explorer, with tabs, a folder tree and a list of files, and adds things Explorer does not
have: it remembers which files you have opened, shows a picture viewer and a slide show, browses
archives like folders, and shows a preview, a plot or the prompt of an AI picture under the list.

## Contents

- [Getting Started](#getting-started)
- [The Main Window](#the-main-window)
- [Tabs](#tabs)
- [The Folder Tree](#the-folder-tree)
- [This PC and Drives](#this-pc-and-drives)
- [The File List](#the-file-list)
  - [Columns](#columns)
  - [Sorting](#sorting)
  - [Filtering the List](#filtering-the-list)
  - [Typing a Name](#typing-a-name)
  - [Selecting Files](#selecting-files)
  - [Hidden Items](#hidden-items)
  - [File Name Extensions](#file-name-extensions)
- [The Info Panel](#the-info-panel)
- [Opening Files](#opening-files)
- [Right-Click Menus](#right-click-menus)
- [Copying, Moving and Deleting](#copying-moving-and-deleting)
- [Renaming](#renaming)
- [Creating Things](#creating-things)
- [Shortcuts](#shortcuts)
- [Archives](#archives)
- [Playlists](#playlists)
- [Bookmarks](#bookmarks)
- [Workspaces and Sessions](#workspaces-and-sessions)
- [The Picture Viewer](#the-picture-viewer)
- [The Play Bar](#the-play-bar)
- [What You Have Read, Ratings and Tags](#what-you-have-read-ratings-and-tags)
- [Dragging and Dropping](#dragging-and-dropping)
- [Settings](#settings)
- [Starting from the Command Line](#starting-from-the-command-line)
- [Keyboard Shortcuts](#keyboard-shortcuts)
- [Log Files](#log-files)
- [Troubleshooting](#troubleshooting)

## Getting Started

### What You Need

- Windows 10 or Windows 11, 64-bit.
- Nothing else is required. A few features use free programs when they are installed:

| Feature | Program |
|---|---|
| Browsing RAR, 7z, cab, gz and tar files | [7-Zip](https://www.7-zip.org) |
| JPEG XL pictures (`.jxl`) | [ImageMagick](https://imagemagick.org) (or Windows' own JPEG XL Image Extension) |
| Preview pictures of videos Windows cannot show | [FFmpeg](https://ffmpeg.org) (`ffmpeg.exe` next to the program or on the PATH) |
| Playing music and video, and the Play bar for them | SweWolf Media Player (a sibling program) |
| **File Info Viewer** in the right-click menus, which shows everything about a file | File Info Viewer (a sibling program) |

SweWolf Media Player and File Info Viewer are looked for next to this program, and then in a folder
next to it with their own name (`..\SwewolfMediaPlayer`, `..\FileInfoViewer`). If a program is missing, the feature that
needs it says so, and everything else keeps working. If **File Info Viewer** is not found, its
menu item still shows; clicking it says where to download the program (https://github.com/SweWolf/FileInfoViewer) and offers to open that page.

### Your First Minutes

1. The program starts on **This PC**: the drives of your computer are listed. Double-click a
   drive, or click a folder in the tree on the left.
2. The files of the folder are shown in the list. Press **Enter** or double-click a file to open it.
3. Click **Folders** in the toolbar if you also want to see the folders in the list. They are
   always in the tree.
4. Right-click a file or folder to see what you can do with it.

## The Main Window

From top to bottom:

- **The menu bar:** Tools, Workspace, Session and Help.
- **The tab strip:** one tab per folder you have open.
- **The toolbar:** Back, Forward, Up, the address bar, **Go**, Refresh, and the buttons
  **Tree**, **Folders**, **.ext**, **New Folder**, **Filter** and **Play**.
- **The folder tree** on the left, with its own header (Up, Sort By, and an **x** that hides the tree).
- **The file list** in the middle.
- **The info panel** under the list: the line "File 14/17", and below it details, a preview picture and more.

The window always starts maximized. The tree and the info panel can be made bigger or smaller by
dragging the line next to them.

### The Address Bar

Type or paste a path and press **Enter**. Quotes around a path are ignored, so you can paste a
path copied from Windows Explorer as it is. You can also type:

- Windows variables such as `%APPDATA%`, `%TEMP%` or `%USERPROFILE%`, also with a path after them.
- Names like `shell:startup`, `shell:sendto`, `shell:downloads` or `shell:fonts`.
- A path inside an archive: `D:\Photos\Trip.zip\Day 1`.

**Esc** in the address bar puts the folder shown back in the box and moves the focus to the list.
**F6** moves the focus to the address bar and selects its text.

### Font Size

Hold **Ctrl** and turn the mouse wheel over the list or over the tree to make its text bigger or
smaller. The list and the tree have separate sizes, and both are remembered.

## Tabs

- **Ctrl+T**, **Ctrl+N** or the **+** button opens a new tab. A new tab always starts on **This PC**.
- **Ctrl+W** or **Ctrl+F4** closes the tab. **Ctrl+Shift+T** brings back the last closed tab, with its history.
- **Ctrl+Tab** and **Ctrl+Shift+Tab** move to the next and previous tab.
- A middle-click on a tab closes it. A middle-click on a folder opens it in a new tab.
- Right-click a tab, or press **F2** with a tab selected, to give it a name of your own with
  **Rename Tab...**. This changes only the text on the tab. **Reset Tab Name** brings back the
  name of the folder.
- The right-click menu of a tab also has **Close Tab**, **Close Other Tabs**, **Close Tabs to the Left**,
  **Close Tabs to the Right** and **Duplicate Tab**. The tab you clicked stays and becomes the active one;
  **Ctrl+Shift+T** brings the closed tabs back, one at a time. **Duplicate Tab** opens the same folder (with the
  same tree root and tab name) in a new tab next to it.

The active tab is shown in bold, with a colored line on top.

## The Folder Tree

The tree starts with **Bookmarks**, **Quick Access** (Desktop, Downloads, Documents, Pictures,
Music and Videos) and **This PC**.

- Click a folder to open it. The tree follows you when you open a folder in any other way.
- **Sort By** sorts the subfolders of the folder you are in (Name, Date Created, Date Modified,
  ascending or descending). The choice is remembered for each folder.
- Folders with the Hidden or System attribute are not listed. A line under the tree says "N hidden
  folders (click to show)".
- Under the tree you see the **Status** of the folder (see
  [What You Have Read](#what-you-have-read-ratings-and-tags)), its **Rating** and **Expectation**
  when it has them, its **Tags**, and a picture of the folder when it has one.
- **Set as Tree Root:** right-click a folder in the tree to make it the top of the tree (also
  possible for an archive). Up and Backspace then stop there. **Reset Tree Root** brings back the normal tree.
- Press **F8**, or click **Tree** in the toolbar, to hide and show the tree.

Typing a letter in the tree selects the next visible folder that starts with it.

## This PC and Drives

Click **This PC** in the tree to see your drives in a list of their own, as in Windows Explorer:
**Name**, **Type**, **File System**, **Total Size**, **Free Space** and **Tracking Data Location**
(see below). Double-click a drive to open it. Right-click a drive for **Open**, **Open in New Tab**, **Open in Windows
Explorer**, the two tracking data items (**Store Tracking Data...** and **Clean Up Tracking Data**), **Properties** and **More Options**. A drive that you connect or remove
shows up or goes by itself within a couple of seconds; **F5** does it at once and also reads the free space again.

### Where the Tracking Data Is Kept

What the program remembers about your files (read marks, ratings, sort orders, column choices and so on, see
[What You Have Read](#what-you-have-read-ratings-and-tags)) is kept in one database on this computer. The column
**Tracking Data Location** says where it is kept for each drive: **This Computer** or **This Drive**.

- A **USB drive, memory card or other removable drive** always keeps it on the drive itself, in the hidden folder
  `.SweWolfFileExplorerData`, so it follows the drive to other computers. The item is shown checked and greyed.
- For any other drive, right-click it and choose **Store Tracking Data (Read Marks, Folder Options, Ratings...)
  Locally on This Drive**. You are asked first. **Yes** moves what is remembered for the drive to the drive, **No**
  keeps new data on the drive but leaves the old data on this computer (it is not used for the drive any more), and
  **Cancel** changes nothing.
- Choose the item again to go back. The data is then moved back to this computer (it is stored there with the drive
  letter, so it only fits the drive while it keeps that letter), and the folder on the drive is renamed
  `.SweWolfFileExplorerData_BAK` as a backup. Nothing is deleted.

### Cleaning Up the Tracking Data of a Drive

If you delete, rename or move files and folders outside this program (for example in Windows Explorer), what the
program remembers about them stays behind. Right-click a drive and choose **Clean Up Tracking Data** to remove it.
You are asked to confirm first. The program then looks at everything it remembers for that drive and removes the
records of files and folders that no longer exist. Files that are still there are never touched, and neither are
the files and folders themselves. The ratings and read marks of a file that you moved or renamed outside the program
are lost, because the program cannot know where it went.

- A copy of the tracking data is saved before anything is removed, in a **Backups** folder next to the data file
  (`cleanup-<date and time>.db`). The last 5 are kept.
- A file inside a compressed file counts as there as long as the compressed file is.
- If the drive is not ready, or looks empty, nothing is checked. If more than half of the records would be removed,
  you are asked once more, because the drive may be missing something that did not come up.
- When it is done, a message tells you how many records were removed. **Cancel** while it checks changes nothing.

## The File List

Press **Enter** or double-click to open a file or folder. **Backspace** goes up one folder in the list, and
**Alt+Up** goes up one folder from anywhere (the tree, the list, the address bar); from the root of a drive it goes to
**This PC**, and **Alt+Left** then takes you back to the drive. **Alt+Left** and **Alt+Right**
go back and forward. **F5** reads the folder again. The tooltips of the toolbar buttons Back, Forward, Up and
Refresh name the folder they go to.

The list is also kept up to date by itself: when another program creates, changes or deletes a
file in the folder, the list follows, and your selection stays.

### Columns

Right-click a column header to choose which columns are shown: Name, Date Modified, Date Created,
Type, Size, and more. **More...** opens a window where you can also set the order and widths.

Some columns are hidden until you choose them:

- **Artist, Title and Track Number** of music files.
- **Length, Bitrate and Dimensions** of music, videos and pictures.
- **Product Name and Product Version** of programs and libraries.
- **Rating and Expectation** (they appear when you set a value).

Dates and times are written the way your Windows regional settings say.

The columns fit their text by themselves. **Ctrl+Num +** fits them again after you have dragged a width.

The columns and the order are remembered **for each folder**, and a folder without its own choice
uses the one of the nearest folder above it that has one. In the column menu:

- **Forget the Columns of This Folder** removes the choice of the folder.
- **Use These Columns as the Default** uses the shown columns for every folder that has no choice of its own.
- **Use These Columns and Sort Order in All Subfolders** gives all folders below this one the same.

### Sorting

Click a column header to sort by it, and click again to turn the order around. A sort you choose is remembered for that folder.

### Filtering the List

Click **Filter** or press **Ctrl+F** to show the filter bar above the list. What you type shows
only the files whose names contain it, anywhere in the name. With several words, every word must
be in the name. **Esc** clears the text, and a second **Esc** closes the bar. You can also press
**Esc** with the focus in the list or the tree to close the bar.

### Typing a Name

With the list or the tree in focus, type the first letters of a name to jump to it. Type the same
letter again to go on to the next name that starts with it.

### Hidden Items

Files and folders with the Hidden or System attribute are not listed. The line "N hidden items
(click to show)" under the list shows them for the folder you are in, faded.

When you select every file shown (**Ctrl+A**) and the folder has hidden items that are not shown, a red
**Warning: N hidden items not selected** appears next to that link: a copy or move of the selection leaves
those items out. Show the hidden items first if you want them included. **Tools > Settings > Files > Hidden Files**
can turn the warning off.

### Selecting Files

**Ctrl+A** selects all the items of the list, wherever the focus is (except in a text box, where it selects the text).
The line under the list says what is selected: "File 14/17" for one item, and for several "7 of 62 files selected"
(or "items", when the list also has folders).

### File Name Extensions

The **.ext** button in the toolbar shows or hides the extensions of known file types (`.txt`,
`.jpg` and so on). The same choice is in **Tools > Settings**. Extensions of special Windows file
types such as `.lnk` are never shown (this can be turned off in the settings).

## The Info Panel

The line under the list says which item is selected ("File 14/17"), how many are selected ("7 of 62 files selected"), or how many items there are.
Below it:

- **Details** on the left: the name of the item, and rows that fit its kind: type, size, dates,
  and for pictures, music and videos also the dimensions, length, bitrate, artist, title and
  so on. With several items selected you see their number and total size. Two small buttons in the corner open
  **Properties** and **File Info Viewer**.
- **A preview picture** of the selected picture, or the cover of a music file, or a frame of a
  video. Click the preview to open the picture in the picture viewer.
- **The plot** of a video or music file, when a `.nfo` file next to it has one.
- **The prompt, negative prompt and seed** of an AI picture made by ComfyUI, Automatic1111 (also
  Forge and SD.Next) or Wan2GP.
- **Where a file came from:** the Zone ID, Host URL and Referrer URL that your web browser saved
  with a downloaded file. They are also remembered, so they show even when the file has lost them.
- **A map pin** when a picture or video says where it was taken. Click the pin to see the place on a
  map in your browser. Right-click the pin to choose another map site, or to copy the coordinates.

The line between the list and the panel can be dragged to make the panel taller. The **x** at the
far right hides the whole panel, and the small **i** button in the info line brings it back.

### Preview Pictures of Videos and Music

For a video or music file, the preview is the first of these that exists: the picture you chose
yourself, a picture inside the file, a picture file next to it (`name-poster.jpg`, `name.jpg`
...), a poster named in its `.nfo` file, the picture Windows shows for it, a frame of the video.
Right-click the preview to **Choose Preview Picture...**, to choose **No Preview Picture**, to
**Use Automatic Preview Picture** again, or to **Save Preview Picture As...**.

For a picture file, right-click the preview and choose **Set as Folder Picture** to make it the picture of the folder.

### The Plot

Double-click the plot area of a video or music file, or right-click it and choose **Edit Plot...**, to
write or change the plot. The text is saved in the `.nfo` file next to the file, which is made
when it does not exist. An existing `.nfo` file is changed carefully: only the plot changes.

## Opening Files

- **Pictures** open in the picture viewer.
- **Music and videos** open in SweWolf Media Player. If it is not found next to the program, the
  program Windows has for the file opens it.
- **Archives** (zip, RAR and so on) are opened like folders.
- **Everything else** opens in the program Windows has for it, started in the folder of the file.
- A `.nfo` file opens in a text editor (Visual Studio Code if you have it, otherwise Notepad).

**Open With...** in the right-click menu lets you choose another program. It is not shown for
programs and scripts, or for folders. **Edit** is shown for the types that have an edit command
(for example `.bat` and `.cmd` files), and opens the file in its editor.

## Right-Click Menus

Right-click a file or a folder in the list, or a folder in the tree. The menus have the same
groups, in this order: opening, cut / copy / paste and shortcuts, delete and rename, read marks, rating,
terminals, new files, properties and **More Options**.

**More Options** (or **Shift + right-click**) shows the real Windows menu, with everything that
other programs have added to it (7-Zip, PowerRename and so on). The first time can take a few seconds.

**Send to** works like in Windows Explorer. Right-click **Send to** and choose **Edit Send To
Menu** to open the folder where its shortcuts are.

**Open in Terminal**, **Open in PowerShell** and **Open in Command Prompt** start the program in the
folder. Right-click one of them (or Shift + click) to start it as the administrator. You can hide
them in **Tools > Settings**.

## Copying, Moving and Deleting

Use **Ctrl+C**, **Ctrl+X** and **Ctrl+V**, the menus, or drag and drop. The clipboard is the one of Windows, so you can copy
files between this program and Windows Explorer.

- **Move/Copy to Folder...** is in the right-click menus of the list and the tree. A window asks where to: type
  or browse for a folder, or take one from your own list (**Add Folder**, **Remove From List**, and arrows to put it in order).
  **Move** is chosen when it opens. It then works like Paste, with the same progress window and questions.
- **Long copies run in the background** with a progress window and a **Cancel** button. Files
  that were finished stay; the one that was being copied is removed again.
- **When a name is taken**, you are asked: **Replace**, **Keep Both**, **Keep Both, Rename Old**, **Skip**
  or **Cancel**. "Do This for All Conflicts" uses your answer for the rest.
  - **Replace** puts the old file in the Recycle Bin (a drive without one says "Replace Permanently").
  - **Keep Both** gives the new file a number: `Name (1).txt`.
  - **Keep Both, Rename Old** gives the new file the name and renames the old one to `Name (old 1).txt`.
- **Delete** moves to the Recycle Bin, with a progress window for many files. Items that cannot go to the Recycle Bin
  (a network drive, a very big file) are listed in the question first.
  **Shift+Delete** deletes for good.
- **Files that belong together:** when you copy or move a video or music file, you are asked whether the files
  that belong to it by name (subtitles, poster) go along (see [Files That Belong Together](#files-that-belong-together)).
- Files you paste from a remote desktop session also work.

Cut, paste, delete and rename keep your read marks and ratings with the files.

## Renaming

**F2** or **Rename...** renames the selected item. For a file, the name and the extension have
separate boxes, so you do not change the extension by accident. The buttons **Upper Case**, **Lower Case**
and **Proper Case** change the name.

### Smart Rename

For a music or video file, **Use Smart Name** suggests a better name:

- Music with tags: `Artist - Title`.
- Video: `Title (Year)`, and `Title - s01e02` for episodes of a series.

Untick the box to get your own name back. **Rename All Selected Music Files and Video Files Using
Smart Rename** does this for all selected files at once, and the button **Undo Rename** in the info
line takes it back.

### Files That Belong Together

When you rename or move a video or music file, the files that belong to it by name (`Movie.srt`,
`Movie.nfo`, `Movie-poster.jpg`) can follow. You are asked first, with the list of the files.

**Tools > Settings > File Names** can also offer to move a renamed video to your
`Videos\Movies` or `Videos\TV Shows` folder.

## Creating Things

- **New Folder** (button, menu, or **Ctrl+Shift+N**).
- **New Text Document...** asks for a name first. Nothing is made if you cancel.
- **New File from Clipboard** makes a text file or a picture (PNG) from what is on the clipboard.
  **Ctrl+V** with no files on the clipboard but a picture or text offers the same.
- **Create Playlist...** makes a playlist (see [Playlists](#playlists)).

## Shortcuts

**Create Shortcut...** in the right-click menus makes a shortcut (`.lnk`) to the selected items in
a folder that you choose. The shortcut is named after the file; a program is named by its product name.
**Tools > Create Shortcut...** makes a shortcut to SweWolf File Explorer itself on the Desktop or in the Start menu.

## Archives

Zip, cbz, RAR, cbr, 7z, cab, gz and tar files are opened like folders: click them in the tree,
or press **Enter** on them in the list. Nothing is unpacked to look inside. An archive inside an
archive works too, and the address bar takes such paths.

- Archives are read-only: cut, delete, rename, paste and new folder are off inside them.
- Copy files out of an archive and paste them into a real folder to unpack them.
- Opening a file inside an archive unpacks it to a temporary folder first.
- RAR, 7z, cab, gz and tar need [7-Zip](https://www.7-zip.org) to be installed.
- Archives with a password cannot be opened yet.

Inside a zip the files are shown in the order they are stored; other archives are sorted by name.

## Playlists

`.m3u` and `.m3u8` files are shown in the tree after the folders. Open one and its files are
shown in the list, in the order of the playlist. A file that is missing is dimmed and is never played.

While a playlist is shown, buttons appear in the info line:

- **Add Files...** adds files after the selected entry.
- **Move Up** and **Move Down** move the selected entries.
- **Save Playlist** (**Ctrl+S**) and **Save Playlist As...** save your changes. You are asked before you leave a playlist with unsaved changes.
- **Delete** (or **Remove from Playlist**) takes the selected entries out of the list. The files themselves are never touched.
- **Ctrl+V** adds the files on the clipboard.

**Create Playlist...** in the right-click menu of a folder makes a new playlist in it, with all
files, the selected files or none to start with.

## Bookmarks

**Bookmarks** is at the top of the tree. A bookmark is a shortcut file in the folder
`%AppData%\SweWolfSoftware\SwewolfFileExplorer\Bookmarks`, so you can also arrange it in Windows
Explorer. Folders in it are groups.

- **Ctrl+D** or **Add to Bookmarks** bookmarks the selected items, or the folder you are in.
- **Ctrl+Shift+O** goes to the Bookmarks node.
- Paste (**Ctrl+V**) or drop files, folders or a web address into the Bookmarks folder or a group to
  make bookmarks. A web address becomes an Internet shortcut with the title and icon of the page.
- A bookmark of a video or music file can start at a time. SweWolf Media Player can make such a bookmark.
- Right-click a bookmark to rename or remove it. Removing a bookmark never touches the file or folder it points to,
  and does not ask.

## Workspaces and Sessions

A **workspace** is a set of folders that you save by hand. A **session** is your open tabs,
remembered all the time.

- **Workspace > Save Current Tabs as Workspace...** saves the open tabs. **Workspace > Open** opens a
  saved one, in new tabs. You can close the current tabs first, or keep them.
- **Session > Start New Session...** starts remembering your tabs under a name. The window title
  then shows the session. **Session > Open** brings back a session, and **Stop Session** stops remembering.
- Both menus have **Delete** and **Show Folder**, and **Create Shortcut to Workspace / Session...**
  makes a shortcut that starts the program with it.

Each workspace and session is one small file that you can copy to another computer. A folder
that is not available (a drive that is not connected) is kept and shown as "(unavailable)"; press **F5** to try again.

## The Picture Viewer

Open a picture to see it full screen. The viewer goes through the pictures of the list, in
the order of the list.

| Key or action | What it does |
|---|---|
| Left / Right, Page Up / Page Down, Space, Backspace, N, P | Previous and next picture |
| Home / End | First and last picture |
| Mouse wheel | Next and previous picture |
| Click on the left third / the rest | Previous / next picture |
| Num + / Num -, **Ctrl** + wheel | Zoom |
| Num * | Fit the picture to the window (also enlarges) |
| Num / | Normal size (fit, never enlarges) |
| Drag | Move a zoomed picture |
| **Ctrl+K** / **Ctrl+L** | Turn the picture (for viewing only: the file is never changed) |
| F11, F, double-click | Full screen, or a window |
| Delete | Move the picture to the Recycle Bin, after a question |
| Esc, X | Close |

### The Right-Click Menu of the Viewer

- **Close**, **Rotate Clockwise**, **Rotate Counterclockwise**, **Toggle Fullscreen**, **Normal Size** and **Stretch** do what the keys above do.
- **Show Newest Picture Automatically** watches the folder of the picture on show. When another program makes a picture
  there (an image generator, a scanner, a phone that syncs) the viewer waits until the file can be read and shows it. Turning
  it on shows the newest picture of the folder at once. It is off every time the viewer opens.
- **Show Host URL** shows the web address the picture was downloaded from in the upper left corner, only the address.
  Click it to open it in your default browser; right-click it to choose another browser or to copy it. Pictures with no known address
  show nothing. The choice is kept for the tab you opened the viewer from, until you close the program.
- **Rating Mode** (**Shift+R**): the number keys rate the picture on show: **2** to **9** at once, **1** waits two seconds
  for a **0** (**1** then **0** is 10), and a **0** on its own removes the rating. The rating is shown, and a second later the viewer goes on to the next
  picture. A picture that already has a rating shows "Current rating: 5" when it comes up. **Rating...** (**R**) opens the rating box.
- **Move/Copy to Folder...** (**M**) opens the same window as in the explorer. After a **move** the viewer goes on to the next
  picture; after a **copy** it stays on the same one.
- **Move to Trash Folder** (**T**) moves the picture into a hidden folder called `Trash` next to it and goes on to the next
  picture. Nothing is deleted. **Take Back from Trash Folder** (**Ctrl+Z**) puts the pictures back, the last one first, as long
  as the viewer is open.
- **Delete** (**Del**) moves the picture to the Recycle Bin, after a question.

When files belong to the picture (`photo.xmp`, `photo.aae`, `photo.jpg.json`, or the RAW file of a JPEG and the other way round),
**Move/Copy to Folder** and **Move to Trash Folder** ask whether they go along: **Yes** takes them too, **No** only the picture,
**Cancel** nothing. The viewer does not move pictures inside an archive or a playlist.

At the end of the list you can **Go to Next Folder**, **Start from Beginning** or **Close**. After 10
seconds the first choice is made for you; move the mouse or press a key to stop the count.

The viewer shows many kinds of pictures: JPEG, PNG, GIF (animated), BMP, WebP, TIFF, icons, SVG, JPEG XL, Photoshop (`.psd`) and
the pictures inside RAW files (NEF, DNG).

## The Play Bar

Click **Play** in the toolbar, or press **Ctrl+P**, to show the Play bar under the info line. It plays the pictures,
music and videos of the list.

- **Previous**, **Play** (it becomes **Pause** while something plays) and **Next**.
- **Play Mode:** Slide Show, Single Play, Repeat, Demo Play, Intro Play or Random Order.
- **Time per File (s)**, **End of List Action** (Go to Next Folder, Start from Beginning or Close),
  **Ask First** and **Time-out (s)**.

Pictures are shown in the picture viewer. Music and videos play in SweWolf Media Player, which
the explorer controls. With the Play bar shown, a double-click on a video or song starts a playlist from that file.

In the picture viewer the **Pause** key (and **P**) pauses a slide show.

## What You Have Read, Ratings and Tags

The program remembers what you have done with your files in a small database (SQLite). It is kept
in `%AppData%\SweWolfSoftware\Library`, and on a USB drive in a hidden folder on the drive itself, so it travels
with the drive. Any other drive can be told to do the same (see
[Where the Tracking Data Is Kept](#where-the-tracking-data-is-kept)).

- **Read marks:** a file counts as read once you open it. Unread files are shown in bold (you can
  choose bold, italic or both in the settings). A folder is **Unread**, **Partly Read** or **Read**, and the
  status is shown under the tree. **Mark as Read** and **Mark as Unread** in the menus change it, also for whole folders.
- **Jump to Last Opened File** goes to the file you opened last in a folder.
- **Rating and Expectation** (**Ctrl+R** and **Ctrl+E**): your own numbers from 1 to 10 for files and
  folders. A rating is a whole number; an expectation may have decimals. An empty box removes the value.
- **Tags of a folder:** **Set Tags...** in the tree menu writes the tags to the `desktop.ini` of the folder, the way Windows does.
  They are shown under the tree.
- **Where a file came from** (the web addresses of a download) is remembered as described under [The Info Panel](#the-info-panel).

Renaming, moving and deleting in this program keeps all of this correct. Changes made by other programs are not seen.

## Dragging and Dropping

- **Drag files out** of the list to other programs, such as Windows Explorer or the upload box of a web page.
- **Drop files in** from Windows Explorer onto the list or onto a folder in the tree: they are
  copied or moved, as in Explorer (**Ctrl** copies, **Shift** moves; by default a move on the same drive and a copy to another). With the **right
  mouse button** a menu asks: **Copy Here**, **Move Here**, **Create Shortcut Here** or **Cancel**.
- **Drop a picture** from a web browser onto the list or a folder in the tree to save it there.
- Dropping files into the Bookmarks folder makes bookmarks.
- **Drop files on a program** (`.exe`, `.com`, `.bat`, `.cmd` or `.scr`) in the list to start it with the dropped files as arguments, as Windows Explorer does.

**Tools > Settings > Drag and Drop** can turn off dropping files into the program.

## Settings

Open **Tools > Settings**. Among other things you can choose:

- **Style:** Light, Dark, System (follows Windows) or Cyber Cyan.
- **Appearance:** the style, and how unread files look (bold, italic).
- **Drag and Drop:** turn off dropping files into the program.
- **Hidden Files:** the extensions that count as hidden, extra extensions to hide inside archives, and the warning when
  all files are selected but hidden items are not.
- **File Names:** hide extensions, the registered name of file types in the Type column, and the offer to move renamed videos.
- **Folder Tree:** whether a right-click selects the folder.
- **Terminal Menu:** which of the three terminals are in the menus.
- **Media Player:** preload SweWolf Media Player in the background at startup (on by default; only shown when the player is found). The first start of the player can be slow while your antivirus program scans its files; the preload does that once, with no window, so videos open fast later.

The settings are saved in `%AppData%\SweWolfSoftware\SwewolfFileExplorer\settings.json`.

## Starting from the Command Line

`SwewolfFileExplorer.exe "path"` starts the program like this:

- A **picture:** the picture viewer opens alone, full screen. Esc ends the program; **Close** in its menu goes on to the explorer.
- A **folder:** the explorer opens with that folder as the top of the tree.
- Any **other file:** its folder opens with the file selected. Nothing is played or opened.
- A **workspace or session file** (`.json`): it is opened.

## Keyboard Shortcuts

| Key | Action |
|---|---|
| Enter / double-click | Open the file or folder |
| Backspace | Up one folder (in the list) |
| Alt+Up | Up one folder, from anywhere |
| Alt+Left / Alt+Right | Back / forward |
| F1 | This help |
| F2 | Rename |
| F5 | Refresh |
| F6 | Go to the address bar |
| F8 | Show or hide the tree |
| Delete / Shift+Delete | Delete to the Recycle Bin / for good |
| Ctrl+A | Select all in the list |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy / cut / paste |
| Ctrl+Shift+C | Copy the path of the selected items |
| Ctrl+Shift+N | New folder |
| Ctrl+T or Ctrl+N | New tab |
| Ctrl+W or Ctrl+F4 | Close the tab |
| Ctrl+Shift+T | Reopen the last closed tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Ctrl+F | Filter bar |
| Esc | Close the filter bar |
| Ctrl+D | Add to Bookmarks |
| Ctrl+Shift+O | Go to the Bookmarks |
| Ctrl+R / Ctrl+E | Rating / expectation |
| Ctrl+P | Show or hide the Play bar |
| Ctrl+Num + | Fit the columns to their text |
| Ctrl + mouse wheel | Bigger or smaller text |
| Alt+Enter | Properties |
| Shift + right-click | The Windows menu |

## Log Files

Errors are written to `errors.log` in
`%AppData%\SweWolfSoftware\SwewolfFileExplorer\Logs`. **Help > Open Log File** and
**Help > Open Log Folder** open them. Nothing but errors is logged, and the file stays small.

## Troubleshooting

**The file list does not show a folder I made in another program.**
Press **F5**. The list follows changes by itself, but a few places (some network drives) cannot be watched.

**A RAR or 7z file cannot be opened: "7-Zip is needed".**
Install [7-Zip](https://www.7-zip.org). The program finds it by itself.

**A JPEG XL picture does not show.**
Install [ImageMagick](https://imagemagick.org) or Windows' JPEG XL Image Extension from the Microsoft Store.

**A video has no preview picture.**
Put `ffmpeg.exe` next to the program or on the PATH, or choose a picture yourself (right-click the preview).

**A music or video file opens in another program.**
SweWolf Media Player was not found next to the program or in `..\SwewolfMediaPlayer`.

**File Info Viewer was not found.**
File Info Viewer is a sibling program. It must be in the same folder as this program or in `..\FileInfoViewer`. The message
gives the download address (https://github.com/SweWolf/FileInfoViewer) and offers to open it in your browser.

**A drive I connected does not show.**
Press **F5**, or wait a few seconds. The drives are checked all the time.

**Something went wrong.**
Look at **Help > Open Log File**.
