# GigaGlade

Find out what fills your drive and clean it up. A portable WinUI 3 app for Windows 10/11.

- **Fast index**: reads the NTFS master file table directly; parallel directory scan for other file systems
- **Cached**: shows the last index at once and catches up from the NTFS change journal
- **Live**: applies file system changes every few seconds (NTFS)
- **Tree and treemap**, always largest first, with synchronised selection; double-click zooms in, right-click zooms out
- **Delete** to the recycle bin by default, permanently after confirmation, with extra warnings for system items
- **Preview** of images, text, PDF, audio and video
- **File types, largest (with filter), oldest, known space hogs, duplicates**
- **Memory and CPU**: the same tree and treemap for RAM and processor use per program (choose them in the drive list)
- **MCP server** for AI assistants: read-only questions about disk, memory and CPU use (see below)

## Download

Get the zip from [Releases](https://github.com/bartfastiel/gigaglade/releases), unpack it anywhere and start `GigaGlade.exe`.
The program is not signed, so Windows SmartScreen may warn on the first start ("More info" → "Run anyway").

## Build

Requires the .NET 10 SDK.

    dotnet publish src/App -c Release -o dist/GigaGlade
    dotnet publish src/Mcp -c Release -o dist/GigaGlade

Copy the folder `dist/GigaGlade` anywhere and start `GigaGlade.exe`. It asks for administrator rights, which reading the master file table needs.

Tests: `dotnet run --project src/Check`

## MCP server

Instructions for Claude instances: [docs/using-with-claude.md](docs/using-with-claude.md).

`gigaglade-mcp.exe` lets an AI assistant such as Claude answer questions like "what fills my drive?" or
"which program uses the most memory?". It speaks the Model Context Protocol on stdin/stdout, needs no
administrator rights and only reads: it has no tool to delete anything.

Tools: `drives`, `folder`, `largest_files`, `oldest_files`, `file_types`, `space_hogs`, `duplicates`, `memory`, `cpu`.

Disk questions are answered from the index that the app caches (saved when the app closes and every
ten minutes while it runs). Folders that are not in an index are scanned on demand.

Example for Claude Code:

    claude mcp add gigaglade -- "C:\path\to\GigaGlade\gigaglade-mcp.exe"

## Good to know

- Sizes are allocated disk space. A file with several hard links is counted once on NTFS.
- "Space hogs" are found by name. They are hints, not a promise that deleting them is safe.
- The app runs as administrator, so it can delete almost anything. Read the confirmation.
- The index (file names and sizes) is cached in `%LOCALAPPDATA%\GigaGlade`. Nothing leaves your computer.
- Without a change journal, the app only reports that something changed; press Rescan.
- Very small files are stored inside the NTFS master file table and occupy no clusters, so they count as 0 B.
- A log with status messages and errors is written to `%LOCALAPPDATA%\GigaGlade\gigaglade.log`.

## Licenses

The source is under the [MIT License](LICENSE). The only package dependency is Microsoft's Windows App SDK (WinUI), including its WebView2 component; it and the .NET runtime come under their own licenses and are not part of this repository; the release download contains them, see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Treemap layout: squarified algorithm as published by Bruls, Huizing and van Wijk (2000), implemented from scratch.
