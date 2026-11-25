using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GigaGlade;

public readonly record struct UsnPosition(long Journal, long Next);

/// <summary>Reads the NTFS master file table and change journal. Read-only; needs administrator rights.</summary>
public static unsafe class Ntfs
{
    const int ChunkBytes = 1 << 20;
    const int BadClusterRecord = 8; // its sparse stream spans the whole volume
    const uint GetVolumeData = 0x90064, GetFileRecord = 0x90068, QueryJournal = 0x900F4, ReadJournal = 0x900BB;

    const int NoBuffering = 0x20000000;

    public static NodeIndex Scan(char drive, IProgress<double>? progress, CancellationToken token)
    {
        using var volume = Open(drive);
        var data = new byte[96];
        if (!Control(volume, GetVolumeData, [], data, out _)) throw new Win32Exception();
        int clusterSize = (int)U32(data, 44), recordSize = (int)U32(data, 48);
        long mftLength = I64(data, 56), mftOffset = I64(data, 64) * clusterSize;

        var first = new byte[Math.Max(recordSize, 4096)];
        fixed (byte* p = first) ReadAt(volume, mftOffset, p, first.Length);

        // Split the MFT into chunks: (volume offset, first record, byte length).
        var chunks = new List<(long Offset, int Record, int Length)>();
        long position = 0;
        var runs = MftRuns(first.AsSpan(0, recordSize));
        foreach (var (lcn, clusters) in runs)
        {
            long runBytes = Math.Min(clusters * clusterSize, mftLength - position);
            if (position % recordSize != 0) throw new NotSupportedException("MFT run is not record aligned");
            for (long o = 0; o < runBytes; o += ChunkBytes)
                chunks.Add((lcn * clusterSize + o, checked((int)((position + o) / recordSize)),
                    (int)Math.Min(ChunkBytes, runBytes - o)));
            position += runBytes;
        }
        if (position < mftLength) throw new NotSupportedException("MFT run list is incomplete");

        var index = new NodeIndex(checked((int)(mftLength / recordSize)), 5, $"{drive}:\\");
        int done = 0;
        Parallel.ForEach(chunks, new ParallelOptions { CancellationToken = token },
            () => (Handle: Open(drive, NoBuffering), Buffer: (nint)NativeMemory.AlignedAlloc(ChunkBytes, 4096), Names: index.Names.CreateWriter()),
            (chunk, _, local) =>
            {
                // Reads must be whole sectors; the valid length may end inside a cluster.
                ReadAt(local.Handle, chunk.Offset, (byte*)local.Buffer, (chunk.Length + 4095) & ~4095);
                for (int o = 0; o + recordSize <= chunk.Length; o += recordSize)
                    ParseRecord(new Span<byte>((byte*)local.Buffer + o, recordSize), chunk.Record + o / recordSize, index, local.Names, true);
                int finished = Interlocked.Increment(ref done);
                if (finished % 16 == 0) progress?.Report((double)finished / chunks.Count);
                return local;
            },
            local => { local.Handle.Dispose(); NativeMemory.AlignedFree((void*)local.Buffer); });
        index.Build();
        return index;
    }

    // ---- change journal ----

    /// <summary>Current journal position, or null if the volume has no active journal.</summary>
    public static UsnPosition? CurrentPosition(char drive)
    {
        using var volume = Open(drive);
        var data = new byte[80];
        return Control(volume, QueryJournal, [], data, out _) ? new(I64(data, 0), I64(data, 16)) : null;
    }

    /// <summary>Collects the records changed since the position; null if the journal no longer reaches back that far.</summary>
    public static (HashSet<long> Records, UsnPosition Position)? ReadChanges(char drive, UsnPosition start)
    {
        using var volume = Open(drive);
        var records = new HashSet<long>();
        var input = new byte[40];
        var output = new byte[1 << 18];
        long next = start.Next;
        while (true)
        {
            BinaryPrimitives.WriteInt64LittleEndian(input, next);
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);
            BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(32), start.Journal);
            if (!Control(volume, ReadJournal, input, output, out int returned)) return null;
            if (returned < 8) return null;
            next = I64(output, 0);
            if (returned == 8) return (records, start with { Next = next });
            for (int o = 8; o + 60 <= returned;)
            {
                int length = (int)U32(output, o);
                if (length < 60) break;
                records.Add(I64(output, o + 8) & 0xFFFFFFFFFFFF);
                o += length;
            }
        }
    }

    /// <summary>Returns an updated copy of the index with the given records read again.</summary>
    public static NodeIndex Apply(char drive, NodeIndex current, IEnumerable<long> records)
    {
        using var volume = Open(drive);
        var index = current.Clone();
        var names = index.Names.CreateWriter();
        var output = new byte[70_000];
        foreach (long record in records)
        {
            if (record is < 24 or > int.MaxValue / 2) continue;
            int id = (int)record;
            index.EnsureCapacity(id + 1);
            long oldOwn = index.Own[id];
            index.Parent[id] = -1;
            index.Own[id] = 0;
            index.Flags[id] = 0;
            if (!FetchRecord(volume, id, output, out var bytes)) continue;
            if ((I64(bytes, 32) & 0xFFFFFFFFFFFF) != 0) continue; // extension; its base record has its own entry
            var extensions = Extensions(bytes, id, out bool incomplete);
            ParseRecord(bytes, id, index, names, false);
            foreach (long extension in extensions)
                if (FetchRecord(volume, extension, output, out bytes) && (I64(bytes, 32) & 0xFFFFFFFFFFFF) == id)
                    ParseRecord(bytes, (int)extension, index, names, false);
            if (incomplete)
            {
                index.Own[id] = oldOwn; // attribute list lives outside the record: keep the last full scan's value
                if (index.Parent[id] == -1)
                {
                    index.Parent[id] = id < current.Capacity ? current.Parent[id] : -1;
                }
            }
        }
        index.Build();
        return index;
    }

    static bool FetchRecord(SafeFileHandle volume, long id, byte[] output, out Span<byte> record)
    {
        var input = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(input, id);
        record = default;
        // For unused records Windows returns the nearest lower one that is in use.
        if (!Control(volume, GetFileRecord, input, output, out _) || (I64(output, 0) & 0xFFFFFFFFFFFF) != id) return false;
        record = output.AsSpan(12, (int)U32(output, 8));
        // Windows may return the record with the update sequence already undone.
        Fixup(record, strict: false);
        return true;
    }

    static HashSet<long> Extensions(Span<byte> r, int self, out bool incomplete)
    {
        var result = new HashSet<long>();
        incomplete = false;
        for (int a = U16(r, 20); a + 24 <= r.Length && U32(r, a) != 0xFFFFFFFF;)
        {
            int length = (int)U32(r, a + 4);
            if (length < 24 || a + length > r.Length) break;
            if (U32(r, a) == 0x20)
            {
                if (r[a + 8] != 0) incomplete = true;
                else
                {
                    var list = r.Slice(a + U16(r, a + 20), (int)U32(r, a + 16));
                    for (int e = 0; e + 26 <= list.Length;)
                    {
                        long reference = I64(list, e + 16) & 0xFFFFFFFFFFFF;
                        if (reference != self) result.Add(reference);
                        int step = U16(list, e + 4);
                        if (step < 26) break;
                        e += step;
                    }
                }
            }
            a += length;
        }
        return result;
    }

    // ---- record parsing ----

    /// <summary>Undoes the NTFS update sequence; in strict mode false for torn or foreign records.</summary>
    static bool Fixup(Span<byte> r, bool strict)
    {
        if (!r[..4].SequenceEqual("FILE"u8)) return false;
        int usa = U16(r, 4), count = U16(r, 6);
        if (usa + count * 2 > r.Length || (count - 1) * 512 > r.Length) return false;
        for (int i = 1; i < count; i++)
        {
            var tail = r.Slice(i * 512 - 2, 2);
            if (tail.SequenceEqual(r.Slice(usa, 2))) r.Slice(usa + i * 2, 2).CopyTo(tail);
            else if (strict) return false;
        }
        return true;
    }

    static void ParseRecord(Span<byte> r, int record, NodeIndex index, NamePool.Writer names, bool raw)
    {
        if (raw && !Fixup(r, true)) return;
        int flags = U16(r, 22);
        if ((flags & 1) == 0 || record == BadClusterRecord) return;

        // Extension records contribute their attributes to the base record.
        long baseRecord = I64(r, 32) & 0xFFFFFFFFFFFF;
        long target = baseRecord == 0 ? record : baseRecord;
        if (target >= index.Capacity) return;
        int node = (int)target;
        // Base and extension records may be parsed on different threads, hence the interlocked writes.
        if (baseRecord == 0)
            SetFlag(ref index.Flags[node], (byte)(NodeIndex.Exists | ((flags & 2) != 0 ? NodeIndex.Directory : 0)));

        for (int a = U16(r, 20); a + 24 <= r.Length;)
        {
            uint type = U32(r, a);
            int length = (int)U32(r, a + 4);
            if (type == 0xFFFFFFFF || length < 24 || a + length > r.Length) break;
            var attribute = r.Slice(a, length);
            bool nonResident = attribute[8] != 0;

            if (type == 0x10 && !nonResident)
                index.Day[node] = (int)(I64(attribute, U16(attribute, 20) + 8) / TimeSpan.TicksPerDay);
            else if (type == 0x30 && !nonResident)
            {
                var value = attribute[U16(attribute, 20)..];
                int nameLength = value[64], nameSpace = value[65];
                int parent = (int)Math.Min(I64(value, 0) & 0xFFFFFFFFFFFF, int.MaxValue);
                // Skip DOS short names; for hard links only the first name counts.
                if (nameSpace != 2 && Interlocked.CompareExchange(ref index.Parent[node], parent, -1) == -1)
                {
                    index.NameRef[node] = names.Add(MemoryMarshal.Cast<byte, char>(value.Slice(66, nameLength * 2)));
                    index.NameLength[node] = (byte)nameLength;
                }
            }
            else if (type is 0x80 or 0xA0 && nonResident && length >= 64 && I64(attribute, 16) == 0)
            {
                // Compressed and sparse streams carry the really allocated size separately.
                bool packed = (U16(attribute, 12) & 0x80FF) != 0 && length >= 72;
                Interlocked.Add(ref index.Own[node], I64(attribute, packed ? 64 : 40));
            }
            else if (type == 0xC0)
                SetFlag(ref index.Flags[node], NodeIndex.Reparse);
            a += length;
        }
    }

    static void SetFlag(ref byte flags, int value)
    {
        byte old;
        do old = flags;
        while (Interlocked.CompareExchange(ref flags, (byte)(old | value), old) != old);
    }

    static List<(long Lcn, long Clusters)> MftRuns(Span<byte> r)
    {
        if (!Fixup(r, true)) throw new InvalidDataException("Invalid MFT record 0");
        for (int a = U16(r, 20); a + 24 <= r.Length && U32(r, a) != 0xFFFFFFFF; a += (int)U32(r, a + 4))
            if (U32(r, a) == 0x80 && r[a + 8] != 0)
                return DecodeRuns(r.Slice(a + U16(r, a + 32), (int)U32(r, a + 4) - U16(r, a + 32)));
        throw new InvalidDataException("MFT has no data attribute");
    }

    public static List<(long Lcn, long Clusters)> DecodeRuns(ReadOnlySpan<byte> s)
    {
        var runs = new List<(long, long)>();
        long lcn = 0;
        for (int i = 0; i < s.Length && s[i] != 0;)
        {
            int lengthBytes = s[i] & 15, offsetBytes = s[i] >> 4;
            i++;
            long clusters = 0, delta = 0;
            for (int b = 0; b < lengthBytes; b++) clusters |= (long)s[i + b] << 8 * b;
            i += lengthBytes;
            for (int b = 0; b < offsetBytes; b++) delta |= (long)s[i + b] << 8 * b;
            if (offsetBytes is > 0 and < 8) delta = delta << 64 - 8 * offsetBytes >> 64 - 8 * offsetBytes;
            i += offsetBytes;
            lcn += delta;
            runs.Add((lcn, clusters));
        }
        return runs;
    }

    static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
    static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
    static long I64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt64LittleEndian(s[o..]);

    // ---- Win32 ----

    static SafeFileHandle Open(char drive, int flags = 0)
    {
        var handle = CreateFileW($@"\\.\{drive}:", 0x80000000 /* GENERIC_READ */, 7, null, 3, flags, 0);
        if (handle.IsInvalid) throw new Win32Exception();
        return handle;
    }

    static void ReadAt(SafeFileHandle handle, long offset, byte* buffer, int length)
    {
        var overlapped = new NativeOverlapped { OffsetLow = (int)offset, OffsetHigh = (int)(offset >> 32) };
        if (!ReadFile(handle, buffer, length, out int read, &overlapped)) throw new Win32Exception();
        if (read != length) throw new EndOfStreamException();
    }

    static bool Control(SafeFileHandle handle, uint code, byte[] input, byte[] output, out int returned)
    {
        fixed (byte* i = input) fixed (byte* o = output)
            return DeviceIoControl(handle, code, i, input.Length, o, output.Length, out returned, null);
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, int share, void* security, int disposition, int flags, nint template);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle handle, byte* buffer, int length, out int read, NativeOverlapped* overlapped);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle handle, uint code, void* input, int inputSize, void* output, int outputSize, out int returned, void* overlapped);
}
