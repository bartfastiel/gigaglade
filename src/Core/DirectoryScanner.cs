using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace GigaGlade;

/// <summary>Parallel directory walk for volumes without MFT access. Hard links are counted per name.</summary>
public static class DirectoryScanner
{
    record struct Entry(string Name, long Size, int Day, bool IsDirectory, bool IsReparse);

    public static NodeIndex Scan(string rootPath, IProgress<int>? progress, CancellationToken token)
    {
        var index = new NodeIndex(1 << 16, 0, rootPath);
        index.Flags[0] = NodeIndex.Exists | NodeIndex.Directory;
        var names = index.Names.CreateWriter();
        int count = 1, pending = 1;
        using var queue = new BlockingCollection<(int Node, string Path)>(new ConcurrentStack<(int, string)>());
        queue.Add((0, rootPath));
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var epoch = new DateTime(1601, 1, 1);

        void Work()
        {
            var entries = new List<Entry>();
            foreach (var (node, path) in queue.GetConsumingEnumerable(token))
            {
                entries.Clear();
                try
                {
                    foreach (var e in new FileSystemEnumerable<Entry>(path, (ref FileSystemEntry e) => new Entry(
                        e.FileName.ToString(),
                        // Offline and recall-on-access placeholders occupy (almost) no local space.
                        e.IsDirectory || ((int)e.Attributes & 0x401000) != 0 ? 0 : (e.Length + 4095) & ~4095L,
                        (int)(e.LastWriteTimeUtc.UtcDateTime - epoch).TotalDays,
                        e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0), options))
                        entries.Add(e);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

                lock (index)
                {
                    index.EnsureCapacity(count + entries.Count);
                    foreach (var e in entries)
                    {
                        int id = count++;
                        index.Parent[id] = node;
                        index.Own[id] = e.Size;
                        index.Day[id] = e.Day;
                        index.NameRef[id] = names.Add(e.Name);
                        index.NameLength[id] = (byte)e.Name.Length;
                        index.Flags[id] = (byte)(NodeIndex.Exists | (e.IsDirectory ? NodeIndex.Directory : 0)
                            | (e.IsReparse ? NodeIndex.Reparse : 0));
                        if (e.IsDirectory && !e.IsReparse)
                        {
                            Interlocked.Increment(ref pending);
                            queue.Add((id, System.IO.Path.Join(path, e.Name)));
                        }
                    }
                }
                progress?.Report(count);
                if (Interlocked.Decrement(ref pending) == 0) queue.CompleteAdding();
            }
        }
        var tasks = Enumerable.Range(0, Math.Min(Environment.ProcessorCount, 16))
            .Select(_ => Task.Factory.StartNew(Work, token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try { Task.WaitAll(tasks); }
        catch (AggregateException e) { throw e.Flatten().InnerExceptions[0]; }
        index.Build();
        return index;
    }
}
