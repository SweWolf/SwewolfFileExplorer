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
  - [Searching](#searching)
  - [Typing a Name](#typing-a-name)
  - [Selecting Files](#selecting-files)
  - [Hidden Items](#hidden-items)
  - [File Name Extensions](#file-name-extensions)
- [Checking a File on VirusTotal](#checking-a-file-on-virustotal)
- [The Info Panel](#the-info-panel)
- [Opening Files](#opening-files)
- [Ripping an Audio CD](#ripping-an-audio-cd)
- [Right-Click Menus](#right-click-menus)
- [Copying, Moving and Deleting](#copying-moving-and-deleting)
  - [Undo](#undo)
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
- [Starting Programs](#starting-programs)
- [Settings](#settings)
  - [Your Own File Type Names and Icons](#your-own-file-type-names-and-icons)
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
  **Tree**, **Folders**, **.ext**, **New Folder**, **Play**, **Filter** and **Search**.
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
smaller. **Ctrl+Plus** and **Ctrl+Minus** do the same (for the tree when it has the focus, else for
the list), and **Ctrl+0** brings the text back to the normal size (14). The list and the tree have
separate sizes, and both are remembered.

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
  when it has them, its **Tags**, and a picture of the folder when it has one. Right-click the folder
  picture for **Choose Folder Picture...**, **No Folder Picture** or **Use Automatic Folder Picture**. Its small **x** hides it,
  and a small button brings it back.
- **Set as Tree Root:** right-click a folder in the tree to make it the top of the tree (also
  possible for an archive). Up and Backspace then stop there. **Reset Tree Root** brings back the normal tree.
- Press **F8**, or click **Tree** in the toolbar, to hide and show the tree.

Typing a letter in the tree selects the next visible folder that starts with it.

## This PC and Drives

Click **This PC** in the tree to see your drives in a list of their own, as in Windows Explorer:
**Name**, **Type**, **File System**, **Total Size**, **Free Space** and **Tracking Data Location**
(see below). Double-click a drive to open it. Right-click a drive for **Open**, **Open in New Tab**, **Open in Windows
Explorer**, the two tracking data items (**Store Tracking Data (Read Marks, Folder Options, Ratings...) Locally on This Drive** and **Clean Up Tracking Data**), **Properties** and **More Options**. A drive that you connect or remove
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

- **Artist, Title and # (Track Number)** of music files. The track numbers are right-aligned.
- **Length, Bitrate and Dimensions** of music, videos and pictures.
- **Date Taken** of pictures: the date and time the camera or phone stored inside the picture. Unlike Date Modified it does not change when you edit or copy the file. Pictures that have none (screenshots, pictures edited by some programs, pictures sent by messaging apps) show nothing. The **Details** block under the list shows it too, above Date Modified.
- **Product Name and Product Version** of programs and libraries.
- **Rating and Expectation** (they appear when you set a value).

Dates and times are written the way your Windows regional settings say.

The columns fit their text by themselves. Right-click a column header and choose **Size All Columns to Fit** to fit them again after you have dragged a width.

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

The bar also has a **Modified** choice: **Later Than** or **Earlier Than** shows a date box and a time box
(hours and minutes, written as Windows writes the time, with AM or PM if your settings use it). The
list shows only the items modified after or before that moment; an item modified at exactly that
minute is included. Later Than starts at 00:00 and Earlier Than at 23:59, so the whole chosen day is
included until you change the time. In the time box the Up and Down arrows or the mouse wheel change
the minutes, and Page Up and Page Down the hours. **Any Date** turns the date filter off. The **Rating** choice filters by your rating: **Any Rating**, **At Least**, **Exactly**,
**At Most** or **Unrated Only**, with a number box (1 to 10). All the filters work together.

### Searching

Click **Search** or press **Ctrl+Shift+F** to look for files and folders in other folders too. (The **Filter** bar
above only works on the folder you are in.) The Search window asks:

- **Look in:** the folder to search. It starts with the folder you are in. Leave it empty to search all
  local drives, and separate several folders with a semicolon (;). **Include Subfolders** is checked by default.
- **Name:** every word must be in the name, in any order and in any case. A word with `*` or `?` must fit
  the whole name, as in Windows (`*.ico` finds names that end in .ico, `alpha*` names that start with alpha, `*alpha*` names that
  have it anywhere). Quotes (`"summer holiday"`) keep words together as a phrase. Only names are searched, not what is inside the files.
- **Search for:** **Files and Folders**, **Files Only** or **Folders Only**; and **Visible Items Only**,
  **Visible and Hidden Items** or **Hidden Items Only**. A hidden folder is not entered unless hidden items are included.
- **Date Modified** and **Date Created**: a **From** and a **To** date. Leave one empty for no limit on that side; the last day is included.

Compressed files (zip, RAR, 7z and so on) are not searched inside, but a compressed file is found by its own name.

Click **Search** in the window and the results open in a **new tab** (named "Search: ..."), so the rest of
the program stays free to use while the search goes on. The items appear as they are found, with the
number found shown above the list, and **Cancel** stops the search (what was found stays). A search that
finds more than 50,000 items stops there: narrow it to see the rest. Folders reached through a link
(junction or symbolic link) are not followed.

The results are an ordinary list with an extra **Location** column that says which folder each item is in.
Open, rename, copy, rate, drag, preview and everything else works as in a folder, with these differences:

- **Delete always asks first** in search results, whatever "Don't ask again" says.
- Opening a folder in the results opens it in another tab, so the results stay.
- **Open File Location** in the right-click menu shows an item in its own folder, in a new tab.
- Paste and New Folder are off (there is no folder). Click a folder in the tree to leave the results.
- Results are not read again by F5. **New Search...** in the bar above the list starts another search in a new tab.

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

## Checking a File on VirusTotal

Right-click a file and choose **Send to VirusTotal** (it has a group of its own in the menu, below
**Add to Bookmarks**). The program works out the file's SHA-256 fingerprint and opens
[VirusTotal](https://www.virustotal.com)'s page for that fingerprint in your web browser. There you see what
the virus scanners say about the file.

- **Nothing is uploaded**, and no account or key is needed: only the fingerprint is sent. A big file takes a
  moment to work out, with a window that has **Cancel**.
- If VirusTotal has never seen the file, its page says so. That does not mean the file is safe, only that nobody has
  sent it there yet.
- It works for one file at a time, also for a file inside a compressed file (it is unpacked to a temporary folder first).

**With your own API key** (**Tools > Settings > Security > VirusTotal**; the link there takes you to VirusTotal's page
where you get a free key) the program asks VirusTotal itself and shows the answer in a message ("0 of 70 scanners
flagged this file"), with an offer to open the full report.

- If VirusTotal has never seen the file, you are asked **"Do you want to upload it, so that it is scanned?"** (Yes is the
  default, so check the file first). Anything you upload becomes visible to VirusTotal's partners and customers, so never upload private files.
  A window with **Cancel** shows while the file is sent and scanned, which can take a minute or two.
- Files up to 650 MB can be uploaded. A free key allows 4 requests a minute and 500 a day, and is meant for personal use.
  Each user needs their own key.
- The key is saved encrypted for your Windows user, not as plain text. Clear the box to remove it.

## The Info Panel

The line under the list says which item is selected ("File 14/17"), how many are selected ("7 of 62 files selected"), or how many items there are. Two small buttons at the right end scroll the list to the top and to the bottom. After **Ctrl+D** a note
appears here with the link **Go to Bookmarks now?**
Below it:

- **Details** on the left: the name of the item, and rows that fit its kind: type, size, dates,
  and for pictures, music and videos also the dimensions, length, bitrate, artist, title (for a video, the title stored inside
  the file) and so on. With several items selected you see their number and total size. Two small buttons in the corner open
  **Properties** and **File Info Viewer**.
- **A preview picture** of the selected picture, or the cover of a music file, or a frame of a
  video. Click the preview to open the picture in the picture viewer.
- **The plot** of a video or music file, when a `.nfo` file next to it has one.
- **The prompt, negative prompt and seed** of an AI picture made by ComfyUI, Automatic1111 (also
  Forge and SD.Next) or Wan2GP. Right-click a box for **Copy** (the selected text, or all of it when nothing is
  selected) and **Copy All Text**. The **Prompt** box also has **Select All Files with This Prompt**: it selects the files of the
  same type in the list that have the same prompt (spaces, punctuation and digits are ignored).
- **Where a file came from:** the Zone ID, Host URL and Referrer URL that your web browser saved
  with a downloaded file. They are also remembered, so they show even when the file has lost them.
- **Audio Source:** the web page of a music file, shown under the Referrer URL. It is read from the
  file's own "audio source" tag (an MP3 from Suno has one). A Suno WAV has no such tag, but its
  comment holds the song's ID, and the page address is made from that. A Suno MP4 video has neither,
  but the song's ID is stored in its provenance data, which is used the same way. Double-click it, or
  right-click it, to open the page in your browser.
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
**Use Automatic Folder Picture** is there too when the folder has a choice, and takes it away.

### The Plot

Double-click the plot area of a video or music file, or right-click it and choose **Edit Plot...**, to
write or change the plot. The text is saved in the `.nfo` file next to the file, which is made
when it does not exist. An existing `.nfo` file is changed carefully: only the plot changes.

When the `.nfo` file exists, the right-click menu of the plot area also has **View/Edit NFO File**, which
opens the whole file in the text editor, as a double-click on the `.nfo` file does.

## Opening Files

- **Pictures** open in the picture viewer.
- **Music and videos** open in SweWolf Media Player; so do the `.cda` tracks of an audio CD. If the player is not found
  (in the same folder as this program, or in a sibling folder called `SwewolfMediaPlayer`), the program Windows has for the file opens it.
- **Archives** (zip, RAR and so on) are opened like folders.
- **Everything else** opens in the program Windows has for it, started in the folder of the file.
- A `.nfo` file opens in a text editor (Visual Studio Code if you have it, otherwise Notepad).

**Open With...** in the right-click menu lets you choose another program. It is not shown for
programs and scripts, or for folders. **Edit** is shown for the types that have an edit command
(for example `.bat` and `.cmd` files), and opens the file in its editor.

## Ripping an Audio CD

While an audio CD is shown (open the CD drive), a **Rip** button appears in the toolbar, to the right of **Play**.
It copies the tracks of the disc to music files on your disk.

1. **Select the tracks** you want in the list. If no track is selected, all tracks are ripped, from top to bottom.
2. Click **Rip**. A window asks:
   - **File Type**: **MP3**, **FLAC (Lossless)** or **M4A (AAC)**, and the **Quality** of that type.
   - **Verify the Rip by Reading Each Track Twice**: slower (about twice as long), but it finds read problems on
     scratched discs. It is checked by default.
   - **Output Folder**: type a folder or use **Browse...**. A folder that does not exist yet is made.

   - When only some of the tracks are selected, two buttons let you choose between **all tracks** of the disc and **the selected tracks**
     (the selected ones are chosen to begin with).

   What you choose is remembered for next time. **OK** starts the ripping, **Cancel** stops it before anything happens.
3. A progress window shows the track being ripped and has **Cancel**. The tracks that are finished stay; the one in
   progress is removed.

**File names and tags.** If the disc is known (it is looked up on MusicBrainz, see **This PC and Drives**), a file is
called `Artist - Title`, for example `Ava Max - Kings & Queens.mp3`; otherwise `Track 02`. A name that is taken gets
` (1)`, ` (2)` and so on: an existing file is never replaced. Each file gets its tags (title, artist, album, album artist,
track number and number of tracks) and the album cover as its picture, if one is known.

**How it went.** When the ripping is over, one message says either that there were **no read problems**, or which
tracks had trouble, for example "Track 7 had read problems (around 2:31). It may click or skip." It then asks whether
you want to open the output folder in a new tab. A track that needed to be read again in some places, and ended up
correct, is mentioned too, but is not a warning.

The program reads the audio from the disc itself and uses **FFmpeg** to make the files, so `ffmpeg.exe` must be next to
the program or in a folder in your PATH. Without it the **Rip** button tells you so.

**Copying tracks to a folder:** a track of an audio CD cannot be copied as a file (it is only a pointer to the audio). If you copy, drag
or send (**Move/Copy to Folder**) tracks of an audio CD to a folder, the same questions appear, and the files are ripped to that folder
(it cannot be changed in the window). Moving works as copying: nothing is removed from the disc. When it is done, a message tells how it went; the folder is not offered in a new tab, but with **Move/Copy to Folder** the box **Open This Folder in a New Tab When Finished** is used.

## Right-Click Menus

Right-click a file or a folder in the list, or a folder in the tree. The menus have the same
groups, in this order: opening, cut / copy / paste and shortcuts, delete and rename, read marks, rating,
terminals, new files, properties and **More Options**.

The menu of the list depends on where you click:

- **On files:** only what acts on the files (open, cut, copy, delete, rename, rating and so on). Paste, the
  terminals and the New items are not shown (Ctrl+V still works).
- **On a folder in the list:** the same as the tree's menu for that folder. **Paste** and the **New** items put
  things inside that folder.
- **On the empty part of the list, or on the info line (the strip with "File 6/7") under the list:** the menu of the
  folder shown (terminals, New items, Paste, Properties and so on). Use the info line when the list is so full
  that there is no empty part left.

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

- **Move/Copy to Folder...** (**Ctrl+M**) is in the right-click menus of the list and the tree. A window asks where to: type
  or browse for a folder, or take one from your own list (**Add Folder**, **Remove From List**, and arrows to put it in order).
  **Move** is chosen when it opens. It then works like Paste, with the same progress window and questions.
  Check **Open This Folder in a New Tab When Finished** to see the result: when the move or copy is done, the folder opens
  in a new tab with the moved or copied items selected. If a tab already shows that folder, the program switches to that
  tab instead. The program remembers the checkbox. Nothing opens if you cancel. (The picture viewer's window has no such checkbox.)
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

### Undo

Right after you delete, rename, move or copy something, or make a new folder or file, a button in the info line
(next to "File 3/14") says what can be undone, for example **Undo Delete (3 items)**. Click it, or press **Ctrl+Z**.
Point at the button to see the names.

- **Only the last step can be undone**, for the whole program (not one per tab). The next thing you do replaces it, and
  a step that cannot be undone empties the button. It is not kept when you close the program. You can change folder or
  tab and it is still there.
- **Delete:** the items are put back from the Recycle Bin, if they are still there. Items deleted for good (Shift+Delete,
  a drive without a Recycle Bin) cannot come back. Ratings and read marks are not brought back.
- **Rename and move:** the items get their old names and places back (a move only within one drive).
- **Copy:** what the copy made is moved to the Recycle Bin, but only while it is unchanged. A file you edited since is kept.
- **New Folder, New Text Document:** removed again, but only while still empty or unchanged.
- Nothing is overwritten and nothing is guessed. If something cannot be undone (it is gone, its old name is taken
  again, the Recycle Bin has been emptied) it is left as it is and listed afterwards.
- A paste where a name was taken (Replace, Keep Both) or that included a folder merge cannot be undone.

## Renaming

**F2** or **Rename...** renames the selected item. For a file, the name and the extension have
separate boxes, so you do not change the extension by accident. The buttons **Upper Case**, **Lower Case**
and **Proper Case** change the name.

### Smart Rename

For a music or video file, **Use Smart Name** suggests a better name:

- Music with tags: `Artist - Title`.
- Video: `Title (Year)`, and `Title - s01e02` for episodes of a series.

Untick the box to get your own name back. **Rename All Selected Music Files and Video Files Using
Smart Rename** does this for all selected files at once. The **Undo** button in the info line takes
a rename back (see [Undo](#undo)).

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
- Right-click a bookmark, a group or the **Bookmarks** node for **Open in New Tab**, **Rename Bookmark...** (or **Rename Group...**),
  **Remove Bookmark** (or **Remove Group**), **Add This Folder to Bookmarks** and **Show Bookmarks Folder**, as far as they fit what
  you clicked. Removing a bookmark never touches the file or folder it points to, and does not ask.
- After **Ctrl+D**, a note with the link **Go to Bookmarks now?** appears in the info line.

## Workspaces and Sessions

A **workspace** is a set of folders that you save by hand. A **session** is your open tabs,
remembered all the time.

- **Workspace > Save Current Tabs as Workspace...** saves the open tabs. Every saved workspace is listed at the bottom of the
  **Workspace** menu: click one to open it in new tabs. You can close the current tabs first, or keep them.
- **Session > Start New Session...** starts remembering your tabs under a name. The window title
  then shows the session. **Stop Session** stops remembering. Saved sessions are listed at the bottom of the **Session** menu (the
  one in use has a check mark): click one to bring it back.
- **Workspace Manager** and **Session Manager** open a window with all the saved ones (see below).
- **Create Shortcut to Workspace "Name"...** is in the Workspace menu once you have opened or saved a workspace, and
  **Create Shortcut to Session "Name"...** is in the Session menu while a session is on. The shortcut starts the program with it.

Each workspace and session is one small file that you can copy to another computer. A folder
that is not available (a drive that is not connected) is kept and shown as "(unavailable)"; press **F5** to try again.

### The Workspace Manager and the Session Manager

The window lists the saved ones (the session in use is marked "(in use)") with these buttons: **Open**, **Rename...**, **Delete**,
**Create Shortcut...** and **Move to Volume...**. A double-click or Enter opens one, **F2** renames and **Delete** deletes. The **Advanced**
check box also shows the number of tabs and when each was last changed, and an **Open Folder** button that opens the folder of the
files in a new tab.

**Move to Volume...** moves the file to a folder you choose, for example on a USB stick, to use it on another computer. The folders on the
drive of the file are saved without the drive letter, so they still work when the stick has another letter there. Tabs on other drives keep
their full path (you are warned first). The program does not remember where the file went: open it by starting the program with it (see
[Starting from the Command Line](#starting-from-the-command-line)) or with a shortcut made by **Create Shortcut...**.

A workspace or session file shown in the list asks **Do you want to open this session now?** when you open it: **Open the Session**,
**Open the File** or **Cancel**.

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
| **Ctrl+K** / **Ctrl+L** | Turn the picture; a question then offers to save it (see below) |
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

**Turning a picture:** after **Ctrl+K** or **Ctrl+L** a panel in the lower right corner asks **Do you want to save the turned picture?**
(**Yes**: Y, **No**: N or Enter, **Cancel**: Esc). A JPEG is turned without losing quality (only its orientation value changes), and a PNG keeps
its prompt and other data. For other kinds (WebP, RAW, Photoshop, SVG, JPEG XL, a file in an archive) the panel offers **Save Copy...**
instead, and the original is never changed. **No** keeps the turn for viewing only.

A picture you delete or move to the Trash folder in the viewer can also be taken back with **Undo** in the explorer window afterwards.

At the end of the list you can **Go to Next Folder**, **Start from Beginning** or **Close**. After 10
seconds the first choice is made for you; move the mouse or press a key to stop the count.

The viewer shows many kinds of pictures: JPEG, PNG, GIF (animated), BMP, WebP, TIFF, icons, SVG, JPEG XL, Photoshop (`.psd`) and
the pictures inside RAW files (NEF, DNG).

## The Play Bar

Click **Play** in the toolbar, or press **Ctrl+P**, to show the Play bar under the info line. It plays the pictures,
music and videos of the list.

If your keyboard has a **Play/Pause** key, it opens the Play bar when the bar is closed. When the bar is open
it works like the bar's **Play** / **Pause** button, and the **Previous Track** and **Next Track** keys work like
the **Previous** and **Next** buttons, as long as this program has the keyboard focus (a media player in front
gets the keys itself).

- **Previous**, **Play** (it becomes **Pause** while something plays) and **Next**.
- **Play Mode:** Continuous Play (also called Slide Show), Single Play, Repeat, Demo Play, Intro Play or Random Order.
- **Time per File (s)**, **End of List Action** (Go to Next Folder, Start from Beginning or Close),
  **Ask First** and **Time-out (s)**.
- **Stop** (the square button) closes the media player and ends the list. **Show Media Player**, at the right, brings the player
  to the front.

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

## Starting Programs

Before the program starts a program (a double-click or Enter on it in the list, a bookmark that points to it, or files dropped on it) it
asks **Do you want to start this program?** (for a program inside a compressed file: **This program is inside a compressed file. Do you want to start it?**). The window is called **Security Check**. It shows the program's own icon (or the icon of its file type), the name and the whole path, and then, a moment later, what can be read from
the file: product, description, company, version, size, date and where it was downloaded from.

- **Start** starts the program (a PowerShell script runs in a PowerShell window that stays open; with .ps1 unchecked in Settings, Windows opens it in Notepad instead). **Cancel** does not. For a script (a batch file, VBScript, JavaScript, PowerShell, Python), or any file type of your own that looks like text, **Open in Visual Studio Code** (or **Open in Notepad**; for a PowerShell script **Open in Windows PowerShell ISE**) shows it in your text editor, so you can read what it does before you start it. **Send to VirusTotal** (above the boxes) checks the file on VirusTotal, as in the right-click menu; only that file is checked, not the files (such as DLL files) it uses.
- **Don't Ask Again for This File** (used only when you press Start) remembers that file by its path.
- **Don't Ask Again for** a file type (used only when you press Start) names the kind of file and its extensions, for example "VBScript Files (.vbs, .vbe)", and
  takes them off the list of file types that count as programs. Put them back in **Tools > Settings > Security**. Not offered for a program inside a compressed file.
- **Don't Ask Again for Any File** (used only when you press Start) turns the question off. Turn it on again in **Tools > Settings > Security**.
- A program **inside a compressed file** has its own setting, **Ask Before Starting a Program Inside a Compressed File**. The question then has a warning
  and **Cancel** is the default button. **Don't Ask Again for Any Program Inside** (the name of the compressed file) stops the question for every program in that one compressed file; "Don't Ask Again for Any Program Inside a Compressed File" turns only that setting off.

In **Tools > Settings > Security** you can also:

- Choose which kinds of files count as programs with check boxes (programs, installers, batch files, VBScript, JavaScript, Windows Script Host
  files, HTML applications, PowerShell scripts, Python scripts, Java programs) and add other file types of your own, separated by semicolons.
  Right-click the box of your own file types for **Clear**.
- Press **Ask Again About All Programs...** to forget every "Don't Ask Again for This File" you chose. It happens at once, not when you press OK.

Windows' own **More Options** menu is not covered: Windows asks for itself there.

## Settings

Open **Tools > Settings**. The window has four tabs: **General**, **Files**, **Menus** and **Security**. Among other things you can choose:

- **Appearance, Style:** Light, Dark, System (follows Windows) or Cyber Cyan.
- **Font:** the font of the file list and the folder tree (the size is set with Ctrl + mouse wheel).
- **Progress Bars:** **Square** or **Rounded**.
- **Unread Files:** how unread files look (bold, italic).
- **Drag and Drop:** turn off dropping files into the program.
- **Hidden Files** (Files tab): the extensions that count as hidden, extra extensions to hide inside archives, and the warning when
  all files are selected but hidden items are not.
- **File Names** (Files tab): hide extensions of known file types, show the registered name of file types in the Type column, the
  offer to move renamed videos, and two check boxes, **Use SweWolf File Explorer's Own Definition of Icons (Recommended)** (Windows 11 only) and
  **Use SweWolf File Explorer's Own Definition of File Type Names (Recommended)**. They give common file types (for example mp3, wav,
  mkv, jpg) a friendlier name and icon. Your own choices are in `FileTypes.json` (see below).
- **Folder Tree** (Menus tab): whether a right-click selects the folder.
- **Security:** your own VirusTotal API key (see [Checking a File on VirusTotal](#checking-a-file-on-virustotal)); ask before a program is started (and, separately, one inside a compressed file), which file types count as programs, and forgetting the "Don't Ask Again" choices (see Starting Programs).
- **Terminal Menu:** which of the three terminals are in the menus.
- **When the Program Is Started Again** (General tab): what happens when you start the program while it is already running (from a shortcut, or from another SweWolf program that opens a folder or file here). **Use the Running Window** (the default) brings the running window to the front; a folder you named opens in a new tab there, a file opens in a new tab with the file selected, a picture opens in the picture viewer, and a workspace or session file opens as usual. **Start a New Window** opens another window, as older versions did. **Ask** shows the question "What do you want to do?" (Use the Running Window / Open a New Window / Cancel); it asks every time, also when you named a folder or file. If a session is on in the running window, you are always asked first (Open a New Window / Use the Running Window / Cancel), because a session records every change to the tabs; the default is Open a New Window. If you start the program with a session file (for example from a shortcut made in the Session Manager), the window that has that session on comes to the front, with no question and whatever the setting says; if no window has it, the session opens in a new window. If the running window cannot take the request at that moment (it is still starting, a dialog is open, or it does not answer), a new window opens anyway. The running window is the one with the same program file in the same place.
- **Updates:** **Check for a New Version at Startup** (on by default). A moment after the program starts it asks GitHub whether a newer version has been released. If there is one, **Version x.y.z Available** appears at the right end of the menu bar; click it to open the release page in your web browser. Nothing is downloaded or installed, and nothing happens when you are offline. **Help > About** also tells you whether you have the latest version.
- **Media Player** (its own tab, the last one; only shown when the player is found): preload SweWolf Media Player in the background at startup (on by default; only shown when the player is found). The first start of the player can be slow while your antivirus program scans its files; the preload does that once, with no window, so videos open fast later.

The settings are saved in `%AppData%\SweWolfSoftware\SwewolfFileExplorer\settings.json`.

### Your Own File Type Names and Icons

To give a file type your own name or icon, make the file `%AppData%\SweWolfSoftware\SwewolfFileExplorer\FileTypes.json`, for example:

```
{ "wav": { "description": "WAV Audio File", "icon": "%SystemRoot%\\System32\\shell32.dll,116" } }
```

The keys are file extensions (with or without the dot). Both `description` and `icon` are optional. An icon is a picture file (`.ico`,
`.png` ...) or `file,number` for an icon inside a DLL or EXE; `%VAR%` names are understood, and a path that is not complete starts in
the folder of `FileTypes.json`. Your list is used first, then the program's own list, then the registered name (if that is chosen), then
what Windows says. The file is read again when you press **F5**. Programs, icons, shortcuts and a few other types always keep
their own icons. A mistake in the file is written to the log file and the normal name or icon is used.

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
| Alt+Home | Go to This PC |
| Alt+Left / Alt+Right | Back / forward |
| F1 | This help |
| F2 | Rename |
| F5 | Refresh |
| F6 | Go to the address bar |
| F8 | Show or hide the tree |
| Delete / Shift+Delete | Delete to the Recycle Bin / for good |
| Ctrl+A | Select all in the list |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy / cut / paste |
| Ctrl+Z | Undo the last delete, rename, move, copy or new folder / file |
| Ctrl+Shift+C | Copy the path of the selected items |
| Ctrl+Shift+N | New folder |
| Ctrl+M | Move/Copy to Folder... |
| Ctrl+S | Save the playlist that is shown |
| Ctrl+T or Ctrl+N | New tab |
| Ctrl+W or Ctrl+F4 | Close the tab |
| Ctrl+Shift+T | Reopen the last closed tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Ctrl+F | Filter bar |
| Ctrl+Shift+F | Search |
| Esc | Close the filter bar |
| Ctrl+D | Add to Bookmarks |
| Ctrl+Shift+O | Go to the Bookmarks |
| Ctrl+R / Ctrl+E | Rating / expectation |
| Ctrl+P | Show or hide the Play bar |
| Play/Pause key | Open the Play bar; with it open: Play / Pause |
| Previous / Next Track keys | With the Play bar open: Previous / Next |
| Ctrl+Plus / Ctrl+Minus | Bigger or smaller text |
| Ctrl+0 | Normal text size |
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

**There is no Rip button, or it says FFmpeg was not found.**
The button shows only while the root of an audio CD is the folder shown. Ripping needs `ffmpeg.exe` next to the program
or in a folder in your PATH.

**A music or video file opens in another program.**
SweWolf Media Player was not found next to the program or in `..\SwewolfMediaPlayer`.

**File Info Viewer was not found.**
File Info Viewer is a sibling program. It must be in the same folder as this program or in `..\FileInfoViewer`. The message
gives the download address (https://github.com/SweWolf/FileInfoViewer) and offers to open it in your browser.

**A drive I connected does not show.**
Press **F5**, or wait a few seconds. The drives are checked all the time.

**Something went wrong.**
Look at **Help > Open Log File**.
