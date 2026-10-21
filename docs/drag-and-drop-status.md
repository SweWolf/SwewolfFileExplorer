# Drag and drop: what works, what does not (2026-10-20)

The user's view (2026-10-20): the most important part is done (dragging **out** to a web browser and other programs).
He is **not a fan of drag and drop inside the app** (easy to slip; copy and paste is safer) and may skip it. To be thought about later.

| Drag and drop | Status |
|---|---|
| Our list to a web browser, Windows Explorer or another program | **Works** (tested by the user: a Windows application and several websites in Chrome) |
| Several selected files, or a folder, out of our list | **Works** (the selection stays intact while pressing: `MainWindow.DragOut.cs`) |
| A file inside a zip / RAR out to another program | Built (unpacked to temp, copy only), **not tried** |
| A picture from a web browser into our list or onto a tree folder | **Works** (`Helpers/DroppedImage`, built 2026-10-18) |
| Files from Windows Explorer into our list or tree | **Images only, as a copy (see below); every other file type is refused** |
| Our list onto a tree folder (copy / move inside the app) | Not implemented |
| A tree folder out to other programs | Not implemented (only the list is a drag source) |
| Dropping onto a tab | Not implemented |

## Why images from Windows Explorer work (probe, 2026-10-20)

A throwaway probe window with a real mouse drag out of a Windows 11 Explorer window showed that Explorer puts **both** the real file and a
browser-style virtual file on every drag: `FileDrop`, `FileName(W)`, `FileContents`, **`FileGroupDescriptorW`**, `Shell IDList Array`,
`ZoneIdentifier` and the drag-image formats. `DroppedImage.CanAccept` treats `FileGroupDescriptorW` as "from a browser" (needed for
Firefox, which also adds a `FileDrop`), so a JPEG dragged from Explorer is accepted and **saved from its bytes** (a copy; the original is not
touched, and the copy may not keep the original's dates: not checked). A text file is refused because its extension is not a picture.
A JPEG in OneDrive could not be tested (the automation did not find the item).

If dropping any file from Explorer is ever wanted: the real list is in `FileDrop`, so it is easy to add; the safe design would be
**copy by default, move only with Shift, and a confirmation**.

## Drag out, how it is built

`MainWindow.DragOut.cs`: left button down on an item, move past the system drag distance, `DragDrop.DoDragDrop` with a `DataObject` that has
`FileDrop`; Copy | Move | Link offered; a Move result reads the list again. A click on one of several selected items is handled on mouse
down and applied on mouse up when there was no drag.
