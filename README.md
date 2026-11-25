# GigaGlade

Find out what fills your drive and clean it up. A portable WinUI 3 app for Windows 10/11.

- **Fast index**: reads the NTFS master file table directly; parallel directory scan for other file systems
- **Cached**: shows the last index at once and catches up from the NTFS change journal
- **Live**: applies file system changes every few seconds (NTFS)
- **Tree and treemap**, always largest first, with synchronised selection; double-click zooms in, right-click zooms out
- **Delete** to the recycle bin by default, permanently after confirmation, with extra warnings for system items
- **Preview** of images, text, PDF, audio and video
- **File types, largest (with filter), oldest, known space hogs, duplicates**

## Build

Requires the .NET 10 SDK.

    dotnet publish src/App -c Release -o dist/GigaGlade

Copy the folder `dist/GigaGlade` anywhere and start `GigaGlade.exe`. It asks for administrator rights, which reading the master file table needs.

Tests: `dotnet run --project src/Check`

## Good to know

- Sizes are allocated disk space. A file with several hard links is counted once on NTFS.
- "Space hogs" are found by name. They are hints, not a promise that deleting them is safe.
- The app runs as administrator, so it can delete almost anything. Read the confirmation.
- The index (file names and sizes) is cached in `%LOCALAPPDATA%\GigaGlade`. Nothing leaves your computer.
- Without a change journal, the app only reports that something changed; press Rescan.

## Licenses

The source is under the [MIT License](LICENSE). The only package dependency is Microsoft's Windows App SDK (WinUI), including its WebView2 component; it and the .NET runtime come under their own licenses and are not part of this repository. Treemap layout: squarified algorithm as published by Bruls, Huizing and van Wijk (2000), implemented from scratch.
