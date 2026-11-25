// Self-tests without a test framework:  Check [scratch folder]
// Benchmark (read-only):                Check bench <mft|dir> <drive letter> [output file]
using System.Diagnostics;
using GigaGlade;

if (args is ["bench", var mode, var drive, .. var rest])
{
    string output;
    try
    {
        char letter = drive[0];
        var watch = Stopwatch.StartNew();
        var position = mode == "mft" ? Ntfs.CurrentPosition(letter) : null;
        var index = mode == "mft" ? Ntfs.Scan(letter, null, default) : DirectoryScanner.Scan($"{letter}:\\", null, default);
        double scan = watch.Elapsed.TotalSeconds;
        watch.Restart();
        int tiles = Treemap.Layout(index, index.Root, 1200, 900).Count;
        double layout = watch.Elapsed.TotalMilliseconds;
        var info = new DriveInfo(drive);
        using var process = Process.GetCurrentProcess();
        output = $"mode={mode} scan={scan:F2}s nodes={index.NodeCount:N0} files={index.FileCount[index.Root]:N0} "
            + $"indexed={index.Total[index.Root] / 1e9:F1}GB used={(info.TotalSize - info.TotalFreeSpace) / 1e9:F1}GB "
            + $"layout={layout:F0}ms tiles={tiles:N0} peakWorkingSet={process.PeakWorkingSet64 / 1e6:F0}MB "
            + $"heapAfterGC={GC.GetTotalMemory(true) / 1e6:F0}MB";
        string cache = Path.GetTempFileName();
        watch.Restart();
        IndexCache.Save(cache, index, position ?? default);
        double save = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        bool loaded = IndexCache.Load(cache) is { } l && l.Index.Total[l.Index.Root] == index.Total[index.Root];
        output += $" cacheSave={save:F0}ms cacheLoad={watch.Elapsed.TotalMilliseconds:F0}ms cacheOk={loaded} cacheSize={new FileInfo(cache).Length / 1e6:F0}MB";
        File.Delete(cache);
        if (position is { } p)
        {
            Thread.Sleep(3000);
            watch.Restart();
            if (Ntfs.ReadChanges(letter, p) is { } changes)
            {
                var updated = Ntfs.Apply(letter, index, changes.Records);
                output += $" update={watch.Elapsed.TotalMilliseconds:F0}ms changed={changes.Records.Count} "
                    + $"indexedAfter={updated.Total[updated.Root] / 1e9:F1}GB nodesAfter={updated.NodeCount:N0}";
            }
        }
        GC.KeepAlive(index);
    }
    catch (Exception e) { output = e.ToString(); }
    Console.WriteLine(output);
    if (rest is [var file]) File.WriteAllText(file, output);
    return 0;
}

int failed = 0;
void Check(bool ok, string name) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}"); if (!ok) failed++; }

var runs = Ntfs.DecodeRuns([0x21, 0x18, 0x34, 0x56, 0x11, 0x10, 0xF0, 0x00]);
Check(runs.SequenceEqual([(0x5634L, 0x18L), (0x5624L, 0x10L)]), "run list with negative delta");

var dir = Path.Combine(args is [var scratch] ? scratch : Path.GetTempPath(), "gigaglade-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(dir, "big"));
Directory.CreateDirectory(Path.Combine(dir, "small"));
File.WriteAllBytes(Path.Combine(dir, "big", "a.bin"), new byte[1_000_000]);
File.WriteAllBytes(Path.Combine(dir, "big", "b.bin"), new byte[300_000]);
File.WriteAllBytes(Path.Combine(dir, "small", "c.txt"), new byte[10_000]);
File.WriteAllBytes(Path.Combine(dir, "small", "d.txt"), new byte[10_000]);
var ix = DirectoryScanner.Scan(dir, null, default);
var top = ix.Children(ix.Root).ToArray();
Check(ix.NodeCount == 7 && ix.FileCount[ix.Root] == 4, "counts");
Check(ix.Name(top[0]).SequenceEqual("big") && ix.Name(top[1]).SequenceEqual("small"), "largest first");
Check(ix.Total[ix.Root] == 1003520 + 303104 + 2 * 12288, "sizes rounded to clusters");
Check(File.Exists(ix.Path(ix.Children(top[0])[0])), "path");
Check(ix.Modified(ix.Children(top[0])[0]).Date == DateTime.UtcNow.Date, "modified date");

var layout2 = Treemap.Layout(ix, ix.Root, 400, 300);
Check(layout2.All(t => t.X >= -.01 && t.Y >= -.01 && t.X + t.W <= 400.01 && t.Y + t.H <= 300.01), "tiles inside bounds");
var leaves = layout2.Where(t => t.Leaf).ToArray();
Check(leaves.Length == 4 && leaves.All(a => leaves.All(b => a == b
    || a.X + a.W <= b.X + .01 || b.X + b.W <= a.X + .01 || a.Y + a.H <= b.Y + .01 || b.Y + b.H <= a.Y + .01)), "leaves do not overlap");
var tileA = layout2.Single(t => t.Node == top[0]);
var tileB = layout2.Single(t => t.Node == top[1]);
Check(Math.Abs(tileA.W * tileA.H / (tileB.W * tileB.H) / ((double)ix.Total[top[0]] / ix.Total[top[1]]) - 1) < .02, "area proportional to size");

Check(Tools.Types(ix, ix.Root)[0] is { Extension: ".bin", Count: 2 }, "types");
Check(Tools.Largest(ix, ix.Root, "*.txt", 9).Count == 2 && Tools.Largest(ix, ix.Root, "", 1)[0] == ix.Children(top[0])[0], "largest and filter");
Check(Tools.Duplicates(ix, ix.Root, 1, null, default) is [{ Length: 2 }], "duplicates");

string cacheFile = Path.Combine(dir, "cache.index");
IndexCache.Save(cacheFile, ix, new(1, 2));
var cached = IndexCache.Load(cacheFile);
Check(cached is { } c && c.Position == new UsnPosition(1, 2) && c.Index.Total[0] == ix.Total[0] && c.Index.Path(top[0]) == ix.Path(top[0]), "cache round trip");
using (var s = File.OpenWrite(cacheFile)) s.SetLength(s.Length / 2);
Check(IndexCache.Load(cacheFile) is null, "truncated cache is rejected");

string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
string sys = Path.GetPathRoot(win)!;
Check(Deletion.Classify(sys) == DeleteRisk.Forbidden && Deletion.Classify(win) == DeleteRisk.Forbidden
    && Deletion.Classify(sys + "$MFT") == DeleteRisk.Forbidden, "forbidden paths");
Check(Deletion.Classify(win + @"\Temp") == DeleteRisk.System && Deletion.Classify(sys + "Users") == DeleteRisk.System
    && Deletion.Classify(sys + @"Program Files\x") == DeleteRisk.System, "system paths");
Check(Deletion.Classify(dir) == DeleteRisk.Normal, "normal path");
bool refused = false;
try { Deletion.Permanently(sys); } catch (InvalidOperationException) { refused = true; }
Check(refused, "deleting a drive root is refused");
Deletion.Permanently(Path.Combine(dir, "small"));
Check(!Directory.Exists(Path.Combine(dir, "small")) && Directory.Exists(Path.Combine(dir, "big")), "permanent delete");
Check(Deletion.Recycle(Path.Combine(dir, "big", "b.bin"), 0) && !File.Exists(Path.Combine(dir, "big", "b.bin")), "recycle");
Console.WriteLine($"test data left in {dir}");
return failed;
