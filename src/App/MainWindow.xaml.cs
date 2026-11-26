using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace GigaGlade;

/// <summary>Scanning, live updates, tree and commands. See the other parts for treemap, result lists and preview.</summary>
public sealed partial class MainWindow : Window
{
    const int MaxChildren = 500;
    static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(5), SaveInterval = TimeSpan.FromMinutes(10);

    public ObservableCollection<TreeRow> Rows { get; } = [];

    NodeIndex? _index;
    char _drive;
    UsageSampler? _sampler; // set while memory or CPU is shown
    UsnPosition? _position; // journal position of the shown index, for saving it
    int _selected = -1;
    bool _syncing, _live;
    readonly HashSet<int> _expanded = [];
    readonly Dictionary<int, int> _limits = [];
    TypeColors _colors = new([]);
    CancellationTokenSource _session = new();
    TaskCompletionSource _wake = new();

    public MainWindow()
    {
        InitializeComponent();
        nint handle = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        double scale = GetDpiForWindow(handle) / 96.0;
        AppWindow.Resize(new((int)(1400 * scale), (int)(860 * scale)));
        AppWindow.SetIcon(Path.Join(AppContext.BaseDirectory, "Assets", "GigaGlade.ico"));

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            var item = new ComboBoxItem
            {
                Content = $"{drive.Name[0]}:  {Tools.FormatSize(drive.TotalFreeSpace)} free of {Tools.FormatSize(drive.TotalSize)}",
                Tag = drive.Name[0],
            };
            DriveBox.Items.Add(item);
            if (drive.Name[0] == 'C') DriveBox.SelectedItem = item;
        }
        foreach (var kind in Enum.GetValues<UsageKind>())
            DriveBox.Items.Add(new ComboBoxItem { Content = new UsageSampler(kind).RootName, Tag = kind });
        if (DriveBox.SelectedItem == null && DriveBox.Items.Count > 0) DriveBox.SelectedIndex = 0;
        Closed += (_, _) =>
        {
            AppLog.Write("window closed");
            _session.Cancel();
            // Keep the cache fresh for the next start and for the MCP server.
            if (_live && _index is { } index && _position is { } position)
                try { IndexCache.Save(IndexCache.DefaultPath(_drive), index, position); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write($"cache not saved: {e.Message}"); }
        };
        // Every status message also goes to the log file.
        // Every status message also goes to the log file, except progress and routine live updates.
        Status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
        {
            if (!Status.Text.StartsWith("Scanning ") && !Status.Text.Contains(" – live, updated ") && !Status.Text.Contains(" – updated "))
                AppLog.Write(Status.Text);
        });
    }

    nint Handle => Win32Interop.GetWindowFromWindowId(AppWindow.Id);

    // ---- scan and live updates ----

    void DriveBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Start(useCache: true);
    void Rescan_Click(object sender, RoutedEventArgs e) => Start(useCache: false);

    async void Start(bool useCache)
    {
        object? tag = (DriveBox.SelectedItem as ComboBoxItem)?.Tag;
        if (tag is not (char or UsageKind)) return;
        _session.Cancel();
        var session = _session = new CancellationTokenSource();
        _live = false;
        bool usage = tag is UsageKind;
        if (useCache || usage || _sampler != null) Reset();
        _sampler = tag is UsageKind kind ? new UsageSampler(kind) : null;
        UpdateCommands();
        try
        {
            if (_sampler is { } sampler) await RunUsage(sampler, session.Token);
            else await Run(_drive = (char)tag, useCache, session.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (session == _session) Status.Text = $"Scan failed: {e.Message}";
        }
        finally
        {
            if (session == _session) Progress.Visibility = Visibility.Collapsed;
        }
    }

    void Reset()
    {
        _index = null;
        _selected = -1;
        _mapRoot = -1;
        _expanded.Clear();
        _limits.Clear();
        _tiles = [];
        Rows.Clear();
        Crumbs.ItemsSource = null;
        MapImage.Source = null;
        MapSelection.Visibility = Visibility.Collapsed;
        ResultList.ItemsSource = null;
        ShowPreview();
    }

    async Task Run(char drive, bool useCache, CancellationToken token)
    {
        string cache = IndexCache.DefaultPath(drive);
        bool ntfs = Environment.IsPrivilegedProcess && new DriveInfo($"{drive}:").DriveFormat == "NTFS";
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;
        var watch = Stopwatch.StartNew();

        UsnPosition? position = null;
        bool needsScan = true;
        if (useCache && await Task.Run(() => IndexCache.Load(cache)) is { } cached)
        {
            token.ThrowIfCancellationRequested();
            Show(cached.Index);
            Status.Text = "Showing the cached index, updating…";
            if (ntfs && cached.Index.Root == 5 && await Task.Run(() => CatchUp(drive, cached.Index, cached.Position)) is { } caughtUp)
            {
                token.ThrowIfCancellationRequested();
                Show(caughtUp.Index);
                position = caughtUp.Position;
                needsScan = false;
            }
        }
        token.ThrowIfCancellationRequested();

        if (needsScan)
        {
            if (_index == null) Status.Text = $"Scanning {drive}:…";
            NodeIndex index;
            if (ntfs)
            {
                Progress.IsIndeterminate = false;
                var progress = new Progress<double>(p => { if (!token.IsCancellationRequested) Progress.Value = p * 100; });
                (index, position) = await Task.Run(() =>
                {
                    var start = Ntfs.CurrentPosition(drive); // before the scan, so that nothing is missed
                    return (Ntfs.Scan(drive, progress, token), start);
                });
            }
            else
            {
                long last = 0;
                var progress = new Progress<int>(n =>
                {
                    if (token.IsCancellationRequested || Environment.TickCount64 - last < 200) return;
                    last = Environment.TickCount64;
                    Status.Text = $"Scanning {drive}: {n:N0} items";
                });
                index = await Task.Run(() => DirectoryScanner.Scan($"{drive}:\\", progress, token));
            }
            token.ThrowIfCancellationRequested();
            // Directory scans number their nodes anew, so ids from the old index are meaningless.
            if (!ntfs || _index?.Root != index.Root) Reset();
            Show(index);
            await Save(cache, position ?? default);
            await ReleaseMemory();
        }
        Progress.Visibility = Visibility.Collapsed;
        Summary($"{(needsScan ? "scanned" : "loaded")} in {watch.Elapsed.TotalSeconds:F1} s");

        _live = position != null;
        _position = position;
        if (position is { } live) await Live(drive, cache, live, token);
        else await WatchOnly(drive, token);
    }

    /// <summary>Memory and CPU: sample all processes every two seconds.</summary>
    async Task RunUsage(UsageSampler sampler, CancellationToken token)
    {
        Status.Text = "Measuring…";
        while (true)
        {
            var index = await Task.Run(sampler.Sample, token);
            token.ThrowIfCancellationRequested();
            Show(index);
            if (Views.Items.IndexOf(Views.SelectedItem) == 2) RefreshResults();
            Status.Text = $"{sampler.ProcessIds.Count:N0} processes, {Fmt(index.Total[index.Root])} in use, {Fmt(sampler.Free)} {sampler.FreeName} – updated {DateTime.Now:T}";
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }

    string Fmt(long value) => _sampler?.Format(value) ?? Tools.FormatSize(value);

    /// <summary>Deleting and the file analyses make no sense for processes.</summary>
    void UpdateCommands()
    {
        bool files = _sampler == null;
        RecycleButton.Visibility = DeleteButton.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < Views.Items.Count; i++)
            Views.Items[i].Visibility = files || i is 0 or 2 ? Visibility.Visible : Visibility.Collapsed;
        if (!files && Views.Items.IndexOf(Views.SelectedItem) is not (0 or 2)) Views.SelectedItem = Views.Items[0];
        FilterBox.PlaceholderText = files ? "Filter, e.g. *.iso" : "Filter, e.g. chrome";
    }

    void Summary(string suffix)
    {
        if (_index is not { } index || _sampler != null) return;
        Status.Text = $"{index.NodeCount:N0} items, {Tools.FormatSize(index.Total[index.Root])} in use, "
            + $"{Tools.FormatSize(new DriveInfo($"{_drive}:").TotalFreeSpace)} free – {suffix}";
    }

    static (NodeIndex Index, UsnPosition Position)? CatchUp(char drive, NodeIndex index, UsnPosition position) =>
        Ntfs.CurrentPosition(drive) is { } now && now.Journal == position.Journal && Ntfs.ReadChanges(drive, position) is { } changes
            ? (changes.Records.Count == 0 ? index : Ntfs.Apply(drive, index, changes.Records), changes.Position) : null;

    /// <summary>Batches journal events: at most one update per interval.</summary>
    async Task Live(char drive, string cache, UsnPosition position, CancellationToken token)
    {
        var lastSave = Stopwatch.StartNew();
        int updates = 0;
        while (true)
        {
            _wake = new();
            await Task.WhenAny(Task.Delay(LiveInterval, token), _wake.Task);
            token.ThrowIfCancellationRequested();
            var index = _index!;
            if (await Task.Run(() => CatchUp(drive, index, position)) is not { } update)
            {
                Status.Text = "The change journal was reset. Press Rescan to update.";
                return;
            }
            token.ThrowIfCancellationRequested();
            position = update.Position;
            _position = position;
            if (update.Index != index)
            {
                Show(update.Index);
                // Every update replaces the index arrays; give the old ones back regularly.
                if (++updates % 6 == 0) await ReleaseMemory();
                Summary($"live, updated {DateTime.Now:T}");
            }
            if (lastSave.Elapsed > SaveInterval)
            {
                await Save(cache, position);
                lastSave.Restart();
            }
        }
    }

    /// <summary>Without a change journal, only point out that something changed.</summary>
    async Task WatchOnly(char drive, CancellationToken token)
    {
        using var watcher = new FileSystemWatcher($"{drive}:\\") { IncludeSubdirectories = true };
        int changed = 0;
        watcher.Created += (_, _) => changed = 1;
        watcher.Deleted += (_, _) => changed = 1;
        watcher.Changed += (_, _) => changed = 1;
        watcher.Renamed += (_, _) => changed = 1;
        watcher.Error += (_, _) => changed = 1;
        watcher.EnableRaisingEvents = true;
        while (true)
        {
            await Task.Delay(LiveInterval, token);
            if (Interlocked.Exchange(ref changed, 0) == 1) Summary("files changed, press Rescan to update");
        }
    }

    /// <summary>Scanning leaves hundreds of megabytes of garbage behind; return it to the system.</summary>
    static Task ReleaseMemory() => Task.Run(() => GC.Collect(2, GCCollectionMode.Aggressive, true, true));

    async Task Save(string cache, UsnPosition position)
    {
        if (_index is not { } index) return;
        try { await Task.Run(() => IndexCache.Save(cache, index, position)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status.Text = $"Cache not saved: {e.Message}"; }
    }

    void Show(NodeIndex index)
    {
        bool first = _index == null;
        _index = index;
        if (first || !index.IsReachable(_mapRoot)) _mapRoot = index.Root;
        if (first) _expanded.Add(index.Root);
        if (_selected >= 0 && !index.IsReachable(_selected))
        {
            _selected = -1;
            ShowPreview();
        }
        _expanded.RemoveWhere(n => !index.IsReachable(n));
        SyncTree();
        UpdateCrumbs();
        // A hidden treemap must not keep the previous index alive.
        if (MapHost.Visibility != Visibility.Visible) _tilesIndex = null;
        _ = RenderMap();
        if (first) RefreshResults();
        if (first) _ = LoadColors(index);
    }

    async Task LoadColors(NodeIndex index)
    {
        if (_sampler != null)
        {
            _colors = TypeColors.ForPrograms();
            await RenderMap();
            return;
        }
        var types = await Task.Run(() => Tools.Types(index, index.Root));
        if (_index?.RootPath != index.RootPath) return;
        _colors = new(types);
        await RenderMap();
    }

    // ---- tree ----

    void SyncTree()
    {
        if (_index is not { } index) return;
        var target = new List<TreeRow>();
        var stack = new Stack<(int Node, int Depth)>([(index.Root, 0)]);
        while (stack.TryPop(out var entry))
        {
            var (node, depth) = entry;
            if (node < 0)
            {
                target.Add(new(node, depth, "Show more…", "", 0, ""));
                continue;
            }
            var children = index.Children(node);
            bool open = _expanded.Contains(node);
            long parentTotal = node == index.Root ? 0 : index.Total[index.Parent[node]];
            target.Add(new(node, depth, index.Name(node).ToString(), Fmt(index.Total[node]),
                parentTotal > 0 ? Math.Round(100.0 * index.Total[node] / parentTotal) : 100,
                children.Length == 0 ? "" : open ? "" : ""));
            if (!open) continue;
            int limit = _limits.GetValueOrDefault(node, MaxChildren);
            if (children.Length > limit) stack.Push((-1 - node, depth + 1));
            for (int i = Math.Min(children.Length, limit) - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
        }

        _syncing = true;
        // Replace only the part that differs, so that the scroll position survives.
        int prefix = 0, common = Math.Min(Rows.Count, target.Count);
        while (prefix < common && Rows[prefix] == target[prefix]) prefix++;
        int suffix = 0;
        while (suffix < common - prefix && Rows[Rows.Count - 1 - suffix] == target[target.Count - 1 - suffix]) suffix++;
        int oldEnd = Rows.Count - suffix, newEnd = target.Count - suffix;
        int both = Math.Min(oldEnd, newEnd);
        for (int i = prefix; i < both; i++) if (Rows[i] != target[i]) Rows[i] = target[i];
        for (int i = oldEnd - 1; i >= both; i--) Rows.RemoveAt(i);
        for (int i = both; i < newEnd; i++) Rows.Insert(i, target[i]);
        Tree.SelectedItem = Rows.FirstOrDefault(r => r.Node == _selected);
        _syncing = false;
    }

    void Toggle(int node)
    {
        if (node < -1 || (node == -1 && _index?.Root == 0))
        {
            int parent = -1 - node;
            _limits[parent] = _limits.GetValueOrDefault(parent, MaxChildren) + MaxChildren;
        }
        else if (!_expanded.Remove(node)) _expanded.Add(node);
        SyncTree();
    }

    static TreeRow? RowOf(object source) => (source as FrameworkElement)?.DataContext as TreeRow;

    void Chevron_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // x:Bind templates set the data context of the container content.
        if (RowOf(sender) is { } row) { Toggle(row.Node); e.Handled = true; }
    }

    void Tree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RowOf(e.OriginalSource) is { } row) Toggle(row.Node);
    }

    void Tree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Tree.SelectedItem is not TreeRow row || _index is not { } index) return;
        if (e.Key == VirtualKey.Delete) Recycle_Click(sender, e);
        else if (e.Key == VirtualKey.Enter || row.Node < 0) { if (e.Key == VirtualKey.Enter) Toggle(row.Node); else return; }
        else if (e.Key == VirtualKey.Right && !_expanded.Contains(row.Node) && index.Children(row.Node).Length > 0) Toggle(row.Node);
        else if (e.Key == VirtualKey.Left && _expanded.Contains(row.Node)) Toggle(row.Node);
        else if (e.Key == VirtualKey.Left && row.Node != index.Root) Select(index.Parent[row.Node]);
        else return;
        e.Handled = true;
    }

    void Tree_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && Tree.SelectedItem is TreeRow row && row.Node >= 0) Select(row.Node, revealInTree: false);
    }

    /// <summary>The single place where the selection changes; keeps tree, treemap and preview in step.</summary>
    void Select(int node, bool revealInTree = true)
    {
        if (_index is not { } index || !index.IsReachable(node)) return;
        _selected = node;
        if (revealInTree)
        {
            for (int n = node; n != index.Root;)
            {
                int parent = index.Parent[n];
                _expanded.Add(parent);
                int position = index.Children(parent).IndexOf(n);
                if (position >= _limits.GetValueOrDefault(parent, MaxChildren)) _limits[parent] = position + 1;
                n = parent;
            }
            SyncTree();
            if (Tree.SelectedItem is { } item) Tree.ScrollIntoView(item);
        }
        ShowMapSelection();
        ShowPreview();
    }

    // ---- commands ----

    /// <summary>For processes, the path of the executable (if Windows tells it).</summary>
    string? SelectedPath => _index is not { } index || _selected < 0 ? null
        : _sampler is { } sampler ? (sampler.ProcessIds.TryGetValue(_selected, out int pid) ? sampler.ExecutablePath(pid) : null)
        : index.Path(_selected);

    void Explorer_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPath is { } path) Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPath is not { } path) return;
        var package = new DataPackage();
        package.SetText(path);
        Clipboard.SetContent(package);
    }

    void Recycle_Click(object sender, RoutedEventArgs e) => _ = DeleteSelected(permanent: false);
    void Delete_Click(object sender, RoutedEventArgs e) => _ = DeleteSelected(permanent: true);

    bool _deleting;

    async Task DeleteSelected(bool permanent)
    {
        if (_deleting || _sampler != null || SelectedPath is not { } path || _index is not { } index) return;
        _deleting = true;
        try
        {
            var risk = Deletion.Classify(path);
            string details = $"{path}\n{Tools.FormatSize(index.Total[_selected])}, {index.FileCount[_selected]:N0} file(s)";
            if (risk == DeleteRisk.Forbidden)
            {
                await Ask("This item cannot be deleted", $"{details}\n\nWindows needs it to work.", null);
                return;
            }
            int node = _selected;
            bool isDirectory = index.IsDirectory(node);
            if (risk == DeleteRisk.System && !await Ask("System item – are you sure?",
                $"{path}\n\nThis belongs to Windows, installed programs or user profiles. Deleting it can break your system.",
                "Continue anyway")) return;
            if (!await Ask(permanent ? "Delete permanently?" : "Move to the recycle bin?",
                permanent ? $"{details}\n\nThis cannot be undone."
                    : $"{details}\n\nSpace is freed only when the recycle bin is emptied.",
                permanent ? "Delete permanently" : "Move to recycle bin")) return;
            // The dialog may have been open for a while.
            if (isDirectory ? !Directory.Exists(path) : !File.Exists(path))
            {
                Status.Text = "The item no longer exists.";
                return;
            }
            nint owner = Handle;
            PreviewMedia.Source = null;
            Status.Text = $"Deleting {path}…";
            bool done = true;
            if (permanent) await Task.Run(() => Deletion.Permanently(path));
            else done = await RunSta(() => Deletion.Recycle(path, owner));
            Status.Text = done ? $"{(permanent ? "Deleted" : "Moved to recycle bin")}: {path}" : "Cancelled.";
            if (done && _selected == node)
            {
                // A recycled item still exists (in the recycle bin), so the index alone would keep it selected.
                _selected = -1;
                Tree.SelectedItem = null;
                ShowMapSelection();
                ShowPreview();
            }
            if (_live) _wake.TrySetResult();
            else if (_index == index && !Path.Exists(path))
            {
                // No change journal: take the item out of the index ourselves.
                var updated = await Task.Run(() =>
                {
                    var copy = index.Clone();
                    copy.Flags[node] = 0;
                    copy.Build();
                    return copy;
                });
                if (_index == index)
                {
                    Show(updated);
                    index = updated; // let go of the old one
                    await ReleaseMemory();
                }
            }
        }
        catch (Exception e) { Status.Text = $"Delete failed: {e.Message}"; }
        finally { _deleting = false; }
    }

    /// <summary>Shell operations need a single-threaded apartment; keep them off the UI thread.</summary>
    static Task<T> RunSta<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(action()); }
            catch (Exception e) { result.SetException(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return result.Task;
    }

    async Task<bool> Ask(string title, string text, string? action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = action == null ? "Close" : "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (action != null) dialog.PrimaryButtonText = action;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    [DllImport("user32")]
    static extern uint GetDpiForWindow(nint window);
}
