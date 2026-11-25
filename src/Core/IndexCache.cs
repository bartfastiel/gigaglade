using System.Runtime.InteropServices;

namespace GigaGlade;

/// <summary>Stores the raw index in one binary file per drive under %LOCALAPPDATA%\GigaGlade.</summary>
public static class IndexCache
{
    const long Magic = 0x3130_5844_4947_4147; // "GAGIDX01"

    public static string DefaultPath(char drive) => Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GigaGlade", $"{drive}.index");

    public static void Save(string path, NodeIndex index, UsnPosition position)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        using (var writer = new BinaryWriter(stream))
        {
            var blocks = index.Names.Blocks;
            writer.Write(Magic);
            writer.Write(position.Journal);
            writer.Write(position.Next);
            writer.Write(index.Root);
            writer.Write(index.RootPath);
            writer.Write(index.Capacity);
            writer.Write(blocks.Length);
            Write(index.Parent); Write(index.Own); Write(index.NameRef);
            Write(index.NameLength); Write(index.Flags); Write(index.Day);
            foreach (var block in blocks) Write(block);
            writer.Write(Magic);

            void Write<T>(T[] array) where T : struct { writer.Flush(); stream.Write(MemoryMarshal.AsBytes(array.AsSpan())); }
        }
        File.Move(temporary, path, true);
    }

    /// <summary>Returns null if there is no usable cache.</summary>
    public static (NodeIndex Index, UsnPosition Position)? Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt64() != Magic) return null;
            var position = new UsnPosition(reader.ReadInt64(), reader.ReadInt64());
            int root = reader.ReadInt32();
            string rootPath = reader.ReadString();
            int capacity = reader.ReadInt32(), blockCount = reader.ReadInt32();
            if (capacity <= root || root < 0 || blockCount < 0
                || 22L * capacity + 2L * NamePool.BlockSize * blockCount > stream.Length) return null;

            var blocks = new char[blockCount][];
            var index = new NodeIndex(capacity, root, rootPath, null);
            Read(index.Parent); Read(index.Own); Read(index.NameRef);
            Read(index.NameLength); Read(index.Flags); Read(index.Day);
            for (int i = 0; i < blockCount; i++) Read(blocks[i] = new char[NamePool.BlockSize]);
            if (reader.ReadInt64() != Magic) return null;
            for (int i = 0; i < capacity; i++)
                if (index.NameRef[i] < 0 || (index.NameLength[i] > 0 && (index.NameRef[i] >> 16 >= blockCount
                    || (index.NameRef[i] & 0xFFFF) + index.NameLength[i] > NamePool.BlockSize))) return null;

            var result = new NodeIndex(0, root, rootPath, new NamePool { Blocks = blocks })
            {
                Parent = index.Parent, Own = index.Own, NameRef = index.NameRef,
                NameLength = index.NameLength, Flags = index.Flags, Day = index.Day,
            };
            result.Build();
            return (result, position);

            void Read<T>(T[] array) where T : struct => stream.ReadExactly(MemoryMarshal.AsBytes(array.AsSpan()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OverflowException or FormatException) { return null; }
    }
}
