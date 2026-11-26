# Using GigaGlade from Claude

This page is for Claude instances (Claude Code, Claude Desktop) that help a user find out what fills their disk,
memory or processor. It explains the GigaGlade MCP server, how to read its answers, and the rules for acting on them.

## What it is

- **GigaGlade.exe**: a Windows app (runs as administrator) that indexes whole drives, shows a tree and a treemap,
  and deletes to the recycle bin after confirmation. It caches its index per drive.
- **gigaglade-mcp.exe**: a small MCP server next to it. It speaks the Model Context Protocol on stdin/stdout,
  needs no administrator rights and **only reads**. There is no tool to delete, move or change anything.

## Setup

Register the server once (Claude Code), with the real path of the unpacked release:

    claude mcp add gigaglade -- "C:\path\to\GigaGlade\gigaglade-mcp.exe"

For Claude Desktop, add it to `claude_desktop_config.json`:

    { "mcpServers": { "gigaglade": { "command": "C:\\path\\to\\GigaGlade\\gigaglade-mcp.exe" } } }

Changing a user's configuration is their decision: show the command and ask before running it.

## Tools

| Tool | Arguments | Answers |
|---|---|---|
| `drives` | – | drives, free space, age of the cached index |
| `folder` | `path`, `limit` | contents of a folder, largest first |
| `largest_files` | `path`, `filter` (text or `*.iso`), `limit` | largest files below a folder |
| `oldest_files` | `path`, `min_size_mb`, `limit` | oldest files (last change) of at least that size |
| `file_types` | `path`, `limit` | space per file extension |
| `space_hogs` | `path`, `min_size_mb` | caches, temp folders, `node_modules`, virtual disks (`.vhdx`), page and hibernation files |
| `duplicates` | `path`, `min_size_mb`, `limit` | files with identical content (SHA-256) |
| `memory` | `limit` | RAM per program |
| `cpu` | `seconds`, `limit` | processor use per program over a short measurement |

`path` defaults to `C:\`. Sizes are allocated disk space (what deleting would free).

## Where the data comes from

- Disk tools answer from the index that the GigaGlade app saved (when it closed, after a scan, and every ten
  minutes while it runs). The first line of every answer says which index and how old it is. If the user
  changed a lot since then, ask them to open GigaGlade briefly (it updates the index in seconds) and close it.
- Folders that are in no index are scanned on demand, which takes longer for big folders.
- `duplicates` reads the files. Use it on specific folders (Downloads, a photo folder), not on a whole drive.
- `memory` and `cpu` measure live. Without administrator rights some system processes report 0 %.

## Rules for advice

- **Never delete anything yourself**, not with this server and not with shell commands, unless the user
  explicitly asks for that exact item. Prefer telling the user what to delete and why; they can do it in the
  GigaGlade app, which moves items to the recycle bin and warns about system items.
- "Space hogs" are found by name. They are hints, not proof that deleting is safe. Name the right way to clean
  up where there is one: `npm cache clean --force`, `docker system prune`, `dotnet nuget locals all --clear`,
  Windows "Disk Cleanup" for Windows Update files, `powercfg /h off` for the hibernation file.
- Do not suggest deleting anything under `C:\Windows`, `C:\Program Files`, `C:\ProgramData` or the page file;
  uninstall programs through Windows Settings instead.
- The recycle bin frees space only when it is emptied.
- Hard links (for example in `C:\Windows\WinSxS`) are counted once; files stored inside the NTFS master file
  table show as 0 B.

## Example

User: "My C: drive is almost full."

1. `drives` – how full, how old the index is.
2. `folder` with `C:\` – the big top-level folders.
3. `space_hogs` with `C:\Users` – caches and virtual disks that can usually be cleaned with their own tools.
4. `largest_files` with `C:\Users\<name>\Downloads` – forgotten installers and archives.
5. Summarise the candidates with sizes and the safest way to remove each; let the user decide.

## Troubleshooting

- The app writes a log to `%LOCALAPPDATA%\GigaGlade\gigaglade.log`.
- The cached indexes are `%LOCALAPPDATA%\GigaGlade\<drive letter>.index`.
- Source and issues: https://github.com/bartfastiel/gigaglade
