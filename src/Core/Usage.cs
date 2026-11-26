using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GigaGlade;

public enum UsageKind { Memory, Cpu }

/// <summary>
/// Samples memory or CPU use of all processes into a NodeIndex: root, one group per process name,
/// one leaf per process. CPU values are hundredths of a percent of the whole machine.
/// Node ids stay the same across samples, so that selection and expanded groups survive.
/// </summary>
public sealed unsafe class UsageSampler(UsageKind kind)
{
    readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, string?> _paths = [];
    Dictionary<int, long> _lastCpu = [];
    long _lastTime;

    public UsageKind Kind => kind;
    public string RootName => kind == UsageKind.Memory ? "Memory (RAM)" : "Processor (CPU)";

    /// <summary>Idle CPU or available memory of the last sample, shown as free area, not as an item.</summary>
    public long Free { get; private set; }

    public string FreeName => kind == UsageKind.Memory ? "available" : "idle";

    /// <summary>Process id of a leaf node, or -1.</summary>
    public Dictionary<int, int> ProcessIds { get; private set; } = [];

    public string? ExecutablePath(int processId)
    {
        if (!_paths.TryGetValue(processId, out var path)) _paths[processId] = path = QueryPath(processId);
        return path;
    }

    public string Format(long value) => kind == UsageKind.Memory ? Tools.FormatSize(value)
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value / 100.0:0.0} %");

    public NodeIndex Sample()
    {
        var processes = Process.GetProcesses();
        long now = Stopwatch.GetTimestamp();
        double elapsed = _lastTime == 0 ? 0 : Stopwatch.GetElapsedTime(_lastTime, now).TotalSeconds;
        var cpu = new Dictionary<int, long>();
        var rows = new List<(int Pid, string Name, long Value)>();
        foreach (var p in processes)
        {
            using (p)
            {
                if (p.Id == 0) continue; // the idle process
                long value;
                if (kind == UsageKind.Memory) value = p.WorkingSet64;
                else
                {
                    long time = CpuTime(p.Id);
                    cpu[p.Id] = time;
                    value = elapsed > 0 && _lastCpu.TryGetValue(p.Id, out long before) && time >= before
                        ? (long)((time - before) / 1e7 / elapsed / Environment.ProcessorCount * 10_000) : 0;
                }
                rows.Add((p.Id, p.ProcessName, value));
            }
        }
        _lastCpu = cpu;
        _lastTime = now;
        foreach (int gone in _paths.Keys.Where(k => !rows.Any(r => r.Pid == k)).ToList()) _paths.Remove(gone);

        long sum = rows.Sum(r => r.Value);
        var extra = new List<(string Name, long Value)>();
        if (kind == UsageKind.Memory)
        {
            var status = new MemoryStatus { Length = (uint)sizeof(MemoryStatus) };
            if (GlobalMemoryStatusEx(ref status))
            {
                long used = (long)(status.Total - status.Available);
                // Working sets share pages, so the processes can add up to more than is in use.
                extra.Add(("(kernel, drivers and other)", Math.Max(0, used - sum)));
                Free = (long)status.Available;
            }
        }
        else Free = elapsed > 0 ? Math.Max(0, 10_000 - sum) : 0;

        // Assign ids first, so that the capacity is known.
        int Id(string key) => _ids.TryGetValue(key, out int id) ? id : _ids[key] = _ids.Count + 1;
        var groups = rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var groupIds = groups.ToDictionary(g => g.Key, g => Id("g:" + g.Key), StringComparer.OrdinalIgnoreCase);
        var leafIds = rows.ToDictionary(r => r.Pid, r => Id($"p:{r.Pid}:{r.Name}"));
        var extraIds = extra.Select(e => Id("x:" + e.Name)).ToArray();

        var index = new NodeIndex(_ids.Count + 1, 0, RootName);
        var names = index.Names.CreateWriter();
        index.Flags[0] = NodeIndex.Exists | NodeIndex.Directory;
        void Set(int id, int parent, string name, long value, byte flags)
        {
            index.Parent[id] = parent;
            index.Own[id] = value;
            index.NameRef[id] = names.Add(name.AsSpan(0, Math.Min(name.Length, 255)));
            index.NameLength[id] = (byte)Math.Min(name.Length, 255);
            index.Flags[id] = flags;
        }
        var processIds = new Dictionary<int, int>();
        foreach (var group in groups)
        {
            Set(groupIds[group.Key], 0, group.Key, 0, NodeIndex.Exists | NodeIndex.Directory);
            foreach (var row in group)
            {
                Set(leafIds[row.Pid], groupIds[group.Key], $"{row.Name} ({row.Pid})", row.Value, NodeIndex.Exists);
                processIds[leafIds[row.Pid]] = row.Pid;
            }
        }
        for (int i = 0; i < extra.Count; i++) Set(extraIds[i], 0, extra[i].Name, extra[i].Value, NodeIndex.Exists);
        index.Build();
        ProcessIds = processIds;
        return index;
    }

    // ---- Win32 ----

    const uint QueryLimitedInformation = 0x1000;

    static long CpuTime(int processId)
    {
        nint handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == 0) return 0;
        try { return GetProcessTimes(handle, out _, out _, out long kernel, out long user) ? kernel + user : 0; }
        finally { CloseHandle(handle); }
    }

    static string? QueryPath(int processId)
    {
        nint handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == 0) return null;
        try
        {
            var buffer = new char[1024];
            int length = buffer.Length;
            fixed (char* p = buffer)
                return QueryFullProcessImageNameW(handle, 0, p, ref length) ? new string(buffer, 0, length) : null;
        }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, Load;
        public ulong Total, Available, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended;
    }

    [DllImport("kernel32", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32", SetLastError = true)]
    static extern nint OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32")]
    static extern bool CloseHandle(nint handle);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(nint process, int flags, char* name, ref int size);
}
