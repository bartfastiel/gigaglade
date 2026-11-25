using System.Security.Cryptography;

namespace GigaGlade;

public sealed record TypeStat(string Extension, long Size, int Count);

/// <summary>Analyses on top of the index.</summary>
public static class Tools
{
    public static string Extension(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 || name.Length - dot > 16 ? "" : name[dot..].ToString().ToLowerInvariant();
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }

    public static IEnumerable<int> Descendants(NodeIndex index, int root)
    {
        var stack = new Stack<int>([root]);
        while (stack.TryPop(out int node))
        {
            yield return node;
            for (int i = 0; i < index.Children(node).Length; i++) stack.Push(index.Children(node)[i]);
        }
    }

    public static IEnumerable<int> Files(NodeIndex index, int root) =>
        Descendants(index, root).Where(n => !index.IsDirectory(n));

    public static List<TypeStat> Types(NodeIndex index, int root)
    {
        var stats = new Dictionary<string, (long Size, int Count)>();
        foreach (int n in Files(index, root))
        {
            var key = Extension(index.Name(n));
            var old = stats.GetValueOrDefault(key);
            stats[key] = (old.Size + index.Total[n], old.Count + 1);
        }
        return [.. stats.Select(s => new TypeStat(s.Key, s.Value.Size, s.Value.Count)).OrderByDescending(s => s.Size)];
    }

    /// <summary>Largest files whose name contains the filter (or matches it, if it has wildcards).</summary>
    public static List<int> Largest(NodeIndex index, int root, string filter, int limit) =>
        [.. Files(index, root).Where(n => Matches(index.Name(n), filter)).OrderByDescending(n => index.Total[n]).Take(limit)];

    public static bool Matches(ReadOnlySpan<char> name, string filter) => filter.Length == 0
        || (filter.AsSpan().IndexOfAny('*', '?') >= 0
            ? System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(filter, name)
            : name.Contains(filter, StringComparison.OrdinalIgnoreCase));

    public static List<int> Oldest(NodeIndex index, int root, long minimumSize, int limit) =>
        [.. Files(index, root).Where(n => index.Total[n] >= minimumSize).OrderBy(n => index.Day[n])
            .ThenByDescending(n => index.Total[n]).Take(limit)];

    // ---- space hogs ----

    static readonly Dictionary<string, string> HogNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node_modules"] = "npm dependencies, restorable",
        ["npm-cache"] = "npm cache",
        [".pnpm-store"] = "pnpm cache",
        ["temp"] = "temporary files",
        ["tmp"] = "temporary files",
        [".gradle"] = "Gradle cache",
        [".m2"] = "Maven cache",
        [".cargo"] = "Cargo cache",
        [".nuget"] = "NuGet cache",
        ["__pycache__"] = "Python cache",
        [".venv"] = "Python environment",
        ["cache"] = "cache",
        ["caches"] = "cache",
        ["code cache"] = "browser cache",
        ["softwaredistribution"] = "Windows Update downloads",
        ["windows.old"] = "previous Windows installation",
        ["$recycle.bin"] = "recycle bin",
        ["hiberfil.sys"] = "hibernation file (powercfg /h off)",
        ["pagefile.sys"] = "page file (managed by Windows)",
        ["memory.dmp"] = "crash dump",
    };

    public static string? HogReason(NodeIndex index, int n)
    {
        var name = index.Name(n);
        if (name.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)) return "virtual disk (WSL, Docker, Hyper-V)";
        return HogNames.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out var reason) ? reason : null;
    }

    /// <summary>Topmost known space hogs of at least the given size.</summary>
    public static List<(int Node, string Reason)> Hogs(NodeIndex index, int root, long minimumSize)
    {
        var result = new List<(int, string)>();
        var stack = new Stack<int>([root]);
        while (stack.TryPop(out int node))
        {
            if (index.Total[node] < minimumSize) continue;
            if (HogReason(index, node) is { } reason) { result.Add((node, reason)); continue; }
            foreach (int c in index.Children(node)) stack.Push(c);
        }
        return [.. result.OrderByDescending(h => index.Total[h.Item1])];
    }

    // ---- duplicates ----

    /// <summary>Groups of identical files (same length and SHA-256), most wasted space first.</summary>
    public static List<int[]> Duplicates(NodeIndex index, int root, long minimumSize, IProgress<string>? progress, CancellationToken token)
    {
        var candidates = Files(index, root)
            .Where(n => index.Total[n] >= minimumSize && (index.Flags[n] & NodeIndex.Reparse) == 0)
            .GroupBy(n => index.Total[n]).Where(g => g.Count() > 1).SelectMany(g => g).ToArray();
        int done = 0;
        var parallel = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 8 };

        List<int[]> Split(int[] nodes, long limit)
        {
            var keys = new string?[nodes.Length];
            done = 0;
            Parallel.For(0, nodes.Length, parallel, i =>
            {
                keys[i] = Key(index.Path(nodes[i]), limit, token);
                int finished = Interlocked.Increment(ref done);
                if (finished % 50 == 0) progress?.Report($"{finished} / {nodes.Length}");
            });
            return [.. nodes.Select((n, i) => (Node: n, Key: keys[i])).Where(x => x.Key != null)
                .GroupBy(x => x.Key).Where(g => g.Count() > 1).Select(g => g.Select(x => x.Node).ToArray())];
        }
        // Cheap pass over the beginning first, then the full content of what is left.
        var remaining = Split(candidates, 1 << 16).SelectMany(g => g).ToArray();
        return [.. Split(remaining, long.MaxValue).OrderByDescending(g => index.Total[g[0]] * (g.Length - 1))];
    }

    static string? Key(string path, long limit, CancellationToken token)
    {
        try
        {
            // Never trigger a download of cloud placeholders.
            if (((int)File.GetAttributes(path) & 0x441000) != 0) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            long total = 0;
            for (int read; total < limit && (read = stream.Read(buffer)) > 0; total += read)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            return $"{stream.Length}:{Convert.ToHexString(hash.GetHashAndReset())}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
