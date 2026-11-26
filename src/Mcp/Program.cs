// GigaGlade MCP server: read-only access to disk usage, memory and CPU for AI assistants.
// Speaks the Model Context Protocol (JSON-RPC 2.0, one message per line) on stdin/stdout.
// It never deletes or changes anything.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GigaGlade;

Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
var server = new Server();
string? line;
while ((line = Console.ReadLine()) != null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    JsonNode? reply;
    try { reply = server.Handle(JsonNode.Parse(line)!.AsObject()); }
    catch (JsonException e) { reply = Server.Error(null, -32700, $"Parse error: {e.Message}"); }
    if (reply != null) Console.WriteLine(reply.ToJsonString());
}

sealed class Server
{
    const string Version = "2025-06-18";
    readonly Dictionary<string, (NodeIndex Index, DateTime Stamp)> _indexes = new(StringComparer.OrdinalIgnoreCase);

    static readonly JsonObject Path = Prop("string", "Folder or drive, e.g. C:\\ or C:\\Users. Default: C:\\");
    static readonly JsonObject Limit = Prop("integer", "Maximum number of results (default 30, at most 500)");

    static readonly (string Name, string Description, JsonObject Properties)[] ToolList =
    [
        ("drives", "Lists drives with size, free space and the age of GigaGlade's index.", []),
        ("folder", "Lists the contents of a folder, largest first, with sizes and file counts.", new() { ["path"] = Path.DeepClone(), ["limit"] = Limit.DeepClone() }),
        ("largest_files", "Largest files below a folder. Optional filter: text or wildcards like *.iso.",
            new() { ["path"] = Path.DeepClone(), ["filter"] = Prop("string", "Name filter, e.g. *.iso or backup"), ["limit"] = Limit.DeepClone() }),
        ("oldest_files", "Oldest files (by last change) of at least min_size_mb below a folder.",
            new() { ["path"] = Path.DeepClone(), ["min_size_mb"] = Prop("number", "Minimum size in MB (default 10)"), ["limit"] = Limit.DeepClone() }),
        ("file_types", "Disk space per file extension below a folder.", new() { ["path"] = Path.DeepClone(), ["limit"] = Limit.DeepClone() }),
        ("space_hogs", "Known space hogs (caches, temp folders, node_modules, virtual disks, ...) below a folder. Hints only; check before deleting.",
            new() { ["path"] = Path.DeepClone(), ["min_size_mb"] = Prop("number", "Minimum size in MB (default 50)") }),
        ("duplicates", "Files with identical content below a folder. Reads the files, so use a specific folder rather than a whole drive.",
            new() { ["path"] = Path.DeepClone(), ["min_size_mb"] = Prop("number", "Minimum size in MB (default 1)"), ["limit"] = Limit.DeepClone() }),
        ("memory", "Memory (RAM) use per program, largest first.", new() { ["limit"] = Limit.DeepClone() }),
        ("cpu", "Processor use per program over a short measurement, largest first.",
            new() { ["seconds"] = Prop("number", "Measurement time in seconds (default 1, at most 10)"), ["limit"] = Limit.DeepClone() }),
    ];

    static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    public JsonNode? Handle(JsonObject request)
    {
        var id = request["id"]?.DeepClone();
        string method = request["method"]?.GetValue<string>() ?? "";
        if (id == null) return null; // notifications need no answer
        try
        {
            return method switch
            {
                "initialize" => Result(id, new JsonObject
                {
                    ["protocolVersion"] = request["params"]?["protocolVersion"]?.GetValue<string>() ?? Version,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "gigaglade", ["version"] = typeof(Server).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" },
                    ["instructions"] = "Read-only disk, memory and CPU analysis. Disk data comes from the index that the GigaGlade app caches; "
                        + "without one, folders are scanned on demand. Nothing is ever deleted; suggest deletions to the user instead.",
                }),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray([.. ToolList.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = t.Properties.DeepClone() },
                        ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
                    })]),
                }),
                "tools/call" => Result(id, Call(request["params"]?["name"]?.GetValue<string>() ?? "", request["params"]?["arguments"] as JsonObject ?? [])),
                _ => Error(id, -32601, $"Unknown method {method}"),
            };
        }
        catch (Exception e) { return Error(id, -32603, e.Message); }
    }

    static JsonObject Result(JsonNode id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    public static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    static JsonObject Text(string text, bool error = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = error,
    };

    JsonObject Call(string name, JsonObject args)
    {
        try
        {
            string path = args["path"]?.GetValue<string>() is { Length: > 0 } p ? System.IO.Path.GetFullPath(p) : @"C:\";
            int limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 30, 1, 500);
            long Mb(string key, double fallback) => (long)((args[key]?.GetValue<double>() ?? fallback) * 1024 * 1024);

            if (name == "drives") return Text(Drives());
            if (name is "memory" or "cpu") return Text(Usage(name == "memory" ? UsageKind.Memory : UsageKind.Cpu,
                Math.Clamp(args["seconds"]?.GetValue<double>() ?? 1, 0.2, 10), limit));

            var (index, node, source) = Find(path);
            string F(long v) => Tools.FormatSize(v);
            var text = new StringBuilder($"{index.Path(node)}: {F(index.Total[node])}, {index.FileCount[node]:N0} files ({source})\n");
            switch (name)
            {
                case "folder":
                    foreach (int c in index.Children(node)[..Math.Min(limit, index.Children(node).Length)])
                        text.Append($"{F(index.Total[c]),10}  {(index.IsDirectory(c) ? $"{index.Name(c)}\\  ({index.FileCount[c]:N0} files)" : index.Name(c).ToString())}\n");
                    break;
                case "largest_files":
                    foreach (int n in GigaGlade.Tools.Largest(index, node, args["filter"]?.GetValue<string>() ?? "", limit))
                        text.Append($"{F(index.Total[n]),10}  {index.Path(n)}\n");
                    break;
                case "oldest_files":
                    foreach (int n in GigaGlade.Tools.Oldest(index, node, Mb("min_size_mb", 10), limit))
                        text.Append($"{index.Modified(n):yyyy-MM-dd}  {F(index.Total[n]),10}  {index.Path(n)}\n");
                    break;
                case "file_types":
                    foreach (var t in GigaGlade.Tools.Types(index, node).Take(limit))
                        text.Append($"{F(t.Size),10}  {t.Count,9:N0} files  {(t.Extension == "" ? "(none)" : t.Extension)}\n");
                    break;
                case "space_hogs":
                    foreach (var (n, reason) in GigaGlade.Tools.Hogs(index, node, Mb("min_size_mb", 50)))
                        text.Append($"{F(index.Total[n]),10}  {index.Path(n)}  ({reason})\n");
                    break;
                case "duplicates":
                    var groups = GigaGlade.Tools.Duplicates(index, node, Mb("min_size_mb", 1), null, default);
                    foreach (var g in groups.Take(limit))
                        text.Append($"{g.Length} copies of {F(index.Total[g[0]])}:\n").AppendJoin("", g.Select(n => $"    {index.Path(n)}\n"));
                    if (groups.Count == 0) text.Append("No duplicates found.\n");
                    break;
                default:
                    return Text($"Unknown tool {name}", true);
            }
            return Text(text.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException)
        {
            return Text(e.Message, true);
        }
    }

    string Drives()
    {
        var text = new StringBuilder();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            var cache = new FileInfo(IndexCache.DefaultPath(d.Name[0]));
            text.Append($"{d.Name}  {d.DriveFormat}  {Tools.FormatSize(d.TotalFreeSpace)} free of {Tools.FormatSize(d.TotalSize)}  "
                + (cache.Exists ? $"index from {cache.LastWriteTime:yyyy-MM-dd HH:mm}" : "no index yet (folders are scanned on demand)") + "\n");
        }
        return text.ToString();
    }

    /// <summary>Finds the node for a path: in the cached index of its drive, else by scanning the folder.</summary>
    (NodeIndex Index, int Node, string Source) Find(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) throw new IOException($"Not found: {path}");
        char drive = char.ToUpperInvariant(path[0]);
        var cache = new FileInfo(IndexCache.DefaultPath(drive));
        if (path.Length > 1 && path[1] == ':' && cache.Exists)
        {
            if (!_indexes.TryGetValue(drive.ToString(), out var entry) || entry.Stamp != cache.LastWriteTimeUtc)
                if (IndexCache.Load(cache.FullName) is { } loaded) _indexes[drive.ToString()] = entry = (loaded.Index, cache.LastWriteTimeUtc);
            if (entry.Index is { } index && Locate(index, path) is int node)
                return (index, node, $"GigaGlade index from {cache.LastWriteTime:yyyy-MM-dd HH:mm}");
        }
        if (!Directory.Exists(path)) throw new IOException($"{path} is not in an index; give a folder to scan it.");
        var scanned = DirectoryScanner.Scan(path, null, default);
        return (scanned, scanned.Root, "scanned just now");
    }

    static int? Locate(NodeIndex index, string path)
    {
        int node = index.Root;
        foreach (string part in path[index.RootPath.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            int next = -1;
            foreach (int c in index.Children(node))
                if (index.Name(c).Equals(part, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
            if (next < 0) return null;
            node = next;
        }
        return node;
    }

    static string Usage(UsageKind kind, double seconds, int limit)
    {
        var sampler = new UsageSampler(kind);
        var index = sampler.Sample();
        if (kind == UsageKind.Cpu)
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            index = sampler.Sample();
        }
        var text = new StringBuilder($"{sampler.RootName}: {sampler.Format(index.Total[index.Root])} in total\n");
        foreach (int group in index.Children(index.Root)[..Math.Min(limit, index.Children(index.Root).Length)])
        {
            int count = index.Children(group).Length;
            text.Append($"{sampler.Format(index.Total[group]),10}  {index.Name(group)}{(count > 1 ? $"  ({count} processes)" : "")}\n");
        }
        return text.ToString();
    }
}
