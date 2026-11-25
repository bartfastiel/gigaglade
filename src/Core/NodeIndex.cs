namespace GigaGlade;

/// <summary>Append-only storage for names; each thread writes through its own Writer.</summary>
public sealed class NamePool
{
    public const int BlockSize = 1 << 16;
    readonly Lock _gate = new();
    char[][] _blocks = [];

    public char[][] Blocks { get => Volatile.Read(ref _blocks); init => _blocks = value; }

    public ReadOnlySpan<char> Get(int reference, int length) => Blocks[reference >> 16].AsSpan(reference & 0xFFFF, length);

    public Writer CreateWriter() => new(this);

    public sealed class Writer(NamePool pool)
    {
        char[] _block = [];
        int _blockIndex, _used;

        public int Add(ReadOnlySpan<char> name)
        {
            if (_used + name.Length > _block.Length)
            {
                _block = new char[BlockSize];
                _used = 0;
                lock (pool._gate)
                {
                    _blockIndex = pool._blocks.Length;
                    Volatile.Write(ref pool._blocks, [.. pool._blocks, _block]);
                }
            }
            name.CopyTo(_block.AsSpan(_used));
            int reference = _blockIndex << 16 | _used;
            _used += name.Length;
            return reference;
        }
    }
}

/// <summary>
/// Compact file tree: one slot per node, children sorted by size descending.
/// For NTFS the slot is the MFT record number, so ids survive live updates.
/// Read-only after Build; updates are applied to a Clone that replaces it.
/// </summary>
public sealed class NodeIndex
{
    public const int Exists = 1, Directory = 2, Reparse = 4;

    public readonly int Root;
    public readonly string RootPath;
    public readonly NamePool Names;
    public int[] Parent;
    public long[] Own;
    public int[] NameRef;
    public byte[] NameLength;
    public byte[] Flags;
    /// <summary>Last write time in days since 1601-01-01.</summary>
    public int[] Day;
    public long[] Total = [];
    public int[] FileCount = [];
    int[] _childStart = [], _children = [];

    public int NodeCount { get; private set; }
    public int Capacity => Parent.Length;

    public NodeIndex(int capacity, int root, string rootPath, NamePool? names = null)
    {
        Root = root;
        RootPath = rootPath;
        Names = names ?? new();
        Parent = new int[capacity];
        Array.Fill(Parent, -1);
        Own = new long[capacity];
        NameRef = new int[capacity];
        NameLength = new byte[capacity];
        Flags = new byte[capacity];
        Day = new int[capacity];
    }

    /// <summary>Copies the raw data (not the built tree); names are shared.</summary>
    public NodeIndex Clone() => new(0, Root, RootPath, Names)
    {
        Parent = (int[])Parent.Clone(),
        Own = (long[])Own.Clone(),
        NameRef = (int[])NameRef.Clone(),
        NameLength = (byte[])NameLength.Clone(),
        Flags = (byte[])Flags.Clone(),
        Day = (int[])Day.Clone(),
    };

    public void EnsureCapacity(int needed)
    {
        int old = Capacity;
        if (needed <= old) return;
        int size = Math.Max(needed, old + old / 4);
        Array.Resize(ref Parent, size);
        Array.Fill(Parent, -1, old, size - old);
        Array.Resize(ref Own, size);
        Array.Resize(ref NameRef, size);
        Array.Resize(ref NameLength, size);
        Array.Resize(ref Flags, size);
        Array.Resize(ref Day, size);
    }

    public bool IsDirectory(int n) => (Flags[n] & Directory) != 0;
    public ReadOnlySpan<int> Children(int n) => _children.AsSpan(_childStart[n], _childStart[n + 1] - _childStart[n]);
    public ReadOnlySpan<char> Name(int n) => n == Root ? RootPath : Names.Get(NameRef[n], NameLength[n]);
    public DateTime Modified(int n) => new DateTime(1601, 1, 1).AddDays(Math.Clamp(Day[n], 0, 3_000_000));

    /// <summary>True if the node is linked up to the root.</summary>
    public bool IsReachable(int n)
    {
        for (int depth = 0; depth < 4096; depth++)
        {
            if (n == Root) return true;
            if ((uint)n >= (uint)Capacity || (Flags[n] & Exists) == 0) return false;
            n = Parent[n];
        }
        return false;
    }

    public string Path(int n)
    {
        var parts = new List<string>();
        for (; n != Root; n = Parent[n]) parts.Add(Name(n).ToString());
        parts.Reverse();
        return System.IO.Path.Join(RootPath, string.Join('\\', parts));
    }

    /// <summary>Links children, sums sizes bottom-up and sorts by size.</summary>
    public void Build()
    {
        int n = Capacity;
        bool Linked(int i) => i != Root && (Flags[i] & Exists) != 0 && (uint)Parent[i] < (uint)n;

        _childStart = new int[n + 1];
        for (int i = 0; i < n; i++) if (Linked(i)) _childStart[Parent[i] + 1]++;
        for (int i = 0; i < n; i++) _childStart[i + 1] += _childStart[i];
        _children = new int[_childStart[n]];
        var next = new int[n];
        Array.Copy(_childStart, next, n);
        for (int i = 0; i < n; i++) if (Linked(i)) _children[next[Parent[i]]++] = i;

        // Breadth-first order guarantees that parents precede their children.
        var order = new int[_children.Length + 1];
        order[0] = Root;
        int count = 1;
        for (int i = 0; i < count; i++)
            foreach (int c in Children(order[i])) order[count++] = c;
        NodeCount = count;

        Total = (long[])Own.Clone();
        FileCount = new int[n];
        for (int i = count - 1; i > 0; i--)
        {
            int node = order[i];
            if (!IsDirectory(node)) FileCount[node] = 1;
            Total[Parent[node]] += Total[node];
            FileCount[Parent[node]] += FileCount[node];
        }
        Parallel.For(0, count, i =>
        {
            int node = order[i];
            int start = _childStart[node], length = _childStart[node + 1] - start;
            if (length > 1)
                _children.AsSpan(start, length).Sort((a, b) => Total[b].CompareTo(Total[a]));
        });
    }
}
