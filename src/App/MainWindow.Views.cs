using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Streams;

namespace GigaGlade;

public sealed partial class MainWindow
{
    const int ResultLimit = 500;

    // ---- treemap ----

    int _mapRoot = -1;
    List<Tile> _tiles = [];
    NodeIndex? _tilesIndex;
    int _mapGeneration;

    void MapHost_SizeChanged(object sender, SizeChangedEventArgs e) => _ = RenderMap();

    async Task RenderMap()
    {
        int generation = ++_mapGeneration;
        if (_index is not { } index || MapHost.Visibility != Visibility.Visible) return;
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        float width = (float)MapHost.ActualWidth, height = (float)MapHost.ActualHeight;
        int w = (int)(width * scale), h = (int)(height * scale), root = _mapRoot;
        if (w < 8 || h < 8) return;
        var colors = _colors;
        var (tiles, pixels) = await Task.Run(() =>
        {
            var t = Treemap.Layout(index, root, width, height);
            return (t, Paint(index, t, colors, w, h, scale));
        });
        if (generation != _mapGeneration) return; // a newer picture is on its way
        var bitmap = new WriteableBitmap(w, h);
        pixels.CopyTo(0, bitmap.PixelBuffer, 0, pixels.Length);
        bitmap.Invalidate();
        MapImage.Source = bitmap;
        _tiles = tiles;
        _tilesIndex = index;
        ShowMapSelection();
    }

    static byte[] Paint(NodeIndex index, List<Tile> tiles, TypeColors colors, int width, int height, double scale)
    {
        var pixels = new byte[width * height * 4];
        foreach (var tile in tiles)
        {
            int x0 = Math.Clamp((int)(tile.X * scale), 0, width), x1 = Math.Clamp((int)((tile.X + tile.W) * scale), 0, width);
            int y0 = Math.Clamp((int)(tile.Y * scale), 0, height), y1 = Math.Clamp((int)((tile.Y + tile.H) * scale), 0, height);
            uint color = !tile.Leaf ? 0x303030u
                : index.IsDirectory(tile.Node) ? 0x606060u : colors.Get(ColorKey(index, tile.Node));
            int r = (int)(color >> 16 & 255), g = (int)(color >> 8 & 255), b = (int)(color & 255);
            for (int y = y0; y < y1; y++)
            {
                // Leaves get a vertical gradient and a dark edge, so that neighbours stay distinguishable.
                int shade = tile.Leaf ? 275 - 70 * (y - y0) / Math.Max(1, y1 - y0) : 256;
                bool edgeRow = tile.Leaf && y == y1 - 1 && y1 - y0 > 2;
                for (int x = x0, o = (y * width + x0) * 4; x < x1; x++, o += 4)
                {
                    int s = edgeRow || (tile.Leaf && x == x1 - 1 && x1 - x0 > 2) ? 140 : shade;
                    pixels[o] = (byte)Math.Min(255, b * s >> 8);
                    pixels[o + 1] = (byte)Math.Min(255, g * s >> 8);
                    pixels[o + 2] = (byte)Math.Min(255, r * s >> 8);
                    pixels[o + 3] = 255;
                }
            }
        }
        return pixels;
    }

    /// <summary>Files are coloured by extension, processes by program.</summary>
    static string ColorKey(NodeIndex index, int node)
    {
        if (index.RootPath.Contains(':')) return Tools.Extension(index.Name(node));
        while (node != index.Root && index.Parent[node] != index.Root) node = index.Parent[node];
        return index.Name(node).ToString();
    }

    Tile? TileAt(Windows.Foundation.Point point)
    {
        if (_tilesIndex == null || _tilesIndex != _index) return null;
        // Parents precede children, so the last match is the deepest one.
        for (int i = _tiles.Count - 1; i >= 0; i--) if (_tiles[i].Contains(point.X, point.Y)) return _tiles[i];
        return null;
    }

    void Map_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MapHost);
        if (point.Properties.IsRightButtonPressed)
        {
            if (_index is { } index && _mapRoot != index.Root) Zoom(index.Parent[_mapRoot]);
        }
        else if (TileAt(point.Position) is { } tile) Select(tile.Node);
    }

    void Map_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (TileAt(e.GetCurrentPoint(MapHost).Position) is not { } tile || _index is not { } index) return;
        if (tile.Node == _hoverTile) return;
        _hoverTile = tile.Node;
        ToolTipService.SetToolTip(MapHost, $"{(_sampler == null ? index.Path(tile.Node) : index.Name(tile.Node).ToString())}\n{Fmt(index.Total[tile.Node])}");
        HighlightRow(tile.Node);
    }

    void Map_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoverTile = -1;
        HighlightRow(-1);
    }

    // ---- hover: highlight the item on the other side ----

    int _hoverTile = -1;
    Panel? _hoverRow;

    /// <summary>Highlights the tree row of the node, or of its nearest visible ancestor if its folder is collapsed.</summary>
    void HighlightRow(int node)
    {
        if (_hoverRow != null) _hoverRow.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _hoverRow = null;
        if (node < 0 || _index is not { } index) return;
        for (int n = node; ; n = index.Parent[n])
        {
            if (Rows.FirstOrDefault(r => r.Node == n) is { } row)
            {
                // Only rows on screen have a container; the tree does not scroll on hover.
                // The row template's own (transparent) background lies above the item's, so colour the template root.
                if (Tree.ContainerFromItem(row) is ListViewItem { ContentTemplateRoot: Panel panel })
                {
                    panel.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorTertiaryBrush"];
                    _hoverRow = panel;
                }
                return;
            }
            if (n == index.Root || (uint)index.Parent[n] >= (uint)index.Capacity) return;
        }
    }

    void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TreeRow { Node: >= 0 } row) ShowMapRect(MapHover, row.Node);
    }

    void Row_PointerExited(object sender, PointerRoutedEventArgs e) => MapHover.Visibility = Visibility.Collapsed;

    /// <summary>Zooms one level towards the clicked item.</summary>
    void Map_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_index is not { } index || TileAt(e.GetPosition(MapHost)) is not { } tile) return;
        int node = tile.Node;
        while (node != index.Root && node != _mapRoot && index.Parent[node] != _mapRoot) node = index.Parent[node];
        if (node != _mapRoot && index.Children(node).Length > 0) Zoom(node);
    }

    void Zoom_Click(object sender, RoutedEventArgs e)
    {
        if (_index is not { } index || _selected < 0) return;
        Zoom(index.IsDirectory(_selected) || _selected == index.Root ? _selected : index.Parent[_selected]);
    }

    void Zoom(int node)
    {
        _mapRoot = node;
        UpdateCrumbs();
        _ = RenderMap();
        if (MapHost.Visibility != Visibility.Visible) RefreshResults();
    }

    void UpdateCrumbs()
    {
        if (_index is not { } index || _mapRoot < 0) return;
        var crumbs = new List<Crumb>();
        for (int n = _mapRoot; ; n = index.Parent[n])
        {
            crumbs.Insert(0, new(n, index.Name(n).ToString()));
            if (n == index.Root) break;
        }
        if (Crumbs.ItemsSource is not List<Crumb> old || !old.SequenceEqual(crumbs)) Crumbs.ItemsSource = crumbs;
    }

    void Crumbs_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is Crumb crumb && _index?.IsReachable(crumb.Node) == true) Zoom(crumb.Node);
    }

    void ShowMapSelection() => ShowMapRect(MapSelection, _selected);

    /// <summary>Frames the node's tile; items too small to be drawn are shown by their nearest drawn ancestor.</summary>
    void ShowMapRect(Microsoft.UI.Xaml.Shapes.Rectangle frame, int node)
    {
        frame.Visibility = Visibility.Collapsed;
        if (_index is not { } index || node < 0 || _tilesIndex != index || !index.IsReachable(node)) return;
        for (int n = node; ; n = index.Parent[n])
        {
            int i = _tiles.FindIndex(t => t.Node == n);
            if (i >= 0)
            {
                var tile = _tiles[i];
                Canvas.SetLeft(frame, tile.X);
                Canvas.SetTop(frame, tile.Y);
                frame.Width = Math.Max(4, tile.W);
                frame.Height = Math.Max(4, tile.H);
                frame.Visibility = Visibility.Visible;
                return;
            }
            if (n == index.Root) return;
        }
    }

    // ---- result lists (for the folder shown in the breadcrumb) ----

    CancellationTokenSource _results = new();

    void Views_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ResultList == null) return; // raised while the window is still loading
        int view = sender.Items.IndexOf(sender.SelectedItem);
        MapHost.Visibility = view == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultList.Visibility = view == 0 ? Visibility.Collapsed : Visibility.Visible;
        FilterBox.Visibility = view == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (view == 0) _ = RenderMap();
        else RefreshResults();
    }

    void Filter_TextChanged(object sender, TextChangedEventArgs e) => RefreshResults();

    async void RefreshResults()
    {
        _results.Cancel();
        var token = (_results = new()).Token;
        int view = Views.Items.IndexOf(Views.SelectedItem), root = _mapRoot;
        ResultList.ItemsSource = null;
        if (_index is not { } index || view <= 0) return;
        string filter = FilterBox.Text.Trim();
        var sampler = _sampler;
        try
        {
            if (view == 2) await Task.Delay(250, token); // wait until typing pauses
            var progress = new Progress<string>(p => { if (!token.IsCancellationRequested) Status.Text = $"Comparing files: {p}"; });
            List<(int Node, string Title, string Detail, long Size, string Extension)> Compute()
            {
                (int, string, string, long, string) Row(int n, string detail) => (n, index.Name(n).ToString(),
                    detail + (sampler == null ? index.Path(n)
                        : sampler.ProcessIds.TryGetValue(n, out int pid) ? sampler.ExecutablePath(pid) ?? "" : ""),
                    index.Total[n], sampler == null ? Tools.Extension(index.Name(n)) : ColorKey(index, n));
                return view switch
                {
                    1 => [.. Tools.Types(index, root).Take(ResultLimit).Select(t =>
                        (-1, t.Extension == "" ? "(no extension)" : t.Extension, $"{t.Count:N0} files", t.Size, t.Extension))],
                    2 => [.. Tools.Largest(index, root, filter, ResultLimit).Select(n => Row(n, ""))],
                    3 => [.. Tools.Oldest(index, root, 10 << 20, ResultLimit).Select(n => Row(n, $"{index.Modified(n):yyyy-MM-dd}  "))],
                    4 => [.. Tools.Hogs(index, root, 50 << 20).Take(ResultLimit).Select(h => Row(h.Node, $"{h.Reason}  –  "))],
                    _ => [.. Tools.Duplicates(index, root, 1 << 20, progress, token).Take(ResultLimit)
                        .SelectMany((g, i) => g.Select(n => Row(n, $"group {i + 1}, {g.Length} copies  –  ")))],
                };
            }
            var rows = await Task.Run(Compute, token);
            if (token.IsCancellationRequested) return;
            ResultList.ItemsSource = rows.Select(r => new ResultRow(r.Node, r.Title, r.Detail, Fmt(r.Size), _colors.Brush(r.Extension))).ToList();
            if (view == 5) Status.Text = $"{rows.Count:N0} files with identical copies (1 MB or larger)";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Status.Text = $"Analysis failed: {e.Message}"; }
    }

    void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.SelectedItem is not ResultRow row) return;
        if (row.Node >= 0) Select(row.Node);
        else if (row.Title.StartsWith('.'))
        {
            FilterBox.Text = "*" + row.Title;
            Views.SelectedItem = Views.Items[2];
        }
    }

    // ---- preview ----

    int _previewGeneration;
    string? _previewPath;

    async void ShowPreview()
    {
        string? path = SelectedPath;
        if (_sampler != null && _index is { } usage && _selected >= 0)
        {
            // Processes: details only, never a preview of the executable.
            _previewPath = null;
            ++_previewGeneration;
            PreviewImage.Visibility = PreviewMedia.Visibility = PreviewScroll.Visibility = Visibility.Collapsed;
            PreviewTitle.Text = $"{usage.Name(_selected)}  –  {Fmt(usage.Total[_selected])}"
                + (_sampler.ProcessIds.TryGetValue(_selected, out int pid) ? $"  –  process {pid}" : $"  –  {usage.Children(_selected).Length} process(es)")
                + (path != null ? $"\n{path}" : "");
            return;
        }
        var current = _index;
        string title = path == null || current == null ? "Select an item to preview it"
            : $"{path}  –  {Fmt(current.Total[_selected])}, {current.FileCount[_selected]:N0} file(s)";
        PreviewTitle.Text = title;
        // The same file again (e.g. after it changed): new title, but keep the loaded content.
        if (path != null && path == _previewPath) return;
        _previewPath = path;
        int generation = ++_previewGeneration;
        PreviewImage.Visibility = PreviewMedia.Visibility = PreviewScroll.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewMedia.Source = null;
        PreviewText.Text = "";
        if (path == null || current is not { } index || index.IsDirectory(_selected)) return;
        try
        {
            // Never trigger a download of cloud placeholders.
            if (await Task.Run(() => ((int)File.GetAttributes(path) & 0x441000) != 0)) return;
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico" or ".tif" or ".tiff":
                    var image = new BitmapImage { DecodePixelHeight = 400 };
                    using (var stream = File.OpenRead(path).AsRandomAccessStream()) await image.SetSourceAsync(stream);
                    if (generation != _previewGeneration) return;
                    PreviewImage.Source = image;
                    PreviewImage.Visibility = Visibility.Visible;
                    break;
                case ".mp4" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".mp3" or ".wav" or ".m4a" or ".flac":
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    if (generation != _previewGeneration) return;
                    PreviewMedia.Source = MediaSource.CreateFromStorageFile(file);
                    PreviewMedia.Visibility = Visibility.Visible;
                    break;
                case ".pdf":
                    var pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
                    var page = new BitmapImage();
                    using (var first = pdf.GetPage(0))
                    using (var memory = new InMemoryRandomAccessStream())
                    {
                        await first.RenderToStreamAsync(memory);
                        await page.SetSourceAsync(memory);
                    }
                    if (generation != _previewGeneration) return;
                    PreviewImage.Source = page;
                    PreviewImage.Visibility = Visibility.Visible;
                    break;
                default:
                    var text = await Task.Run(() => ReadText(path));
                    if (generation != _previewGeneration || text == null) return;
                    PreviewText.Text = text;
                    PreviewScroll.Visibility = Visibility.Visible;
                    break;
            }
        }
        catch (Exception e)
        {
            if (generation != _previewGeneration) return;
            PreviewText.Text = $"No preview: {e.Message}";
            PreviewScroll.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Beginning of a text file, or null if it looks binary.</summary>
    static string? ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[16 << 10];
        int length = stream.ReadAtLeast(buffer, buffer.Length, false);
        bool utf16 = length >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF));
        if (!utf16 && buffer.AsSpan(0, length).Contains((byte)0)) return null;
        using var reader = new StreamReader(new MemoryStream(buffer, 0, length), true);
        return reader.ReadToEnd();
    }
}
