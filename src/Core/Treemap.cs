namespace GigaGlade;

public readonly record struct Tile(int Node, float X, float Y, float W, float H, int Depth, bool Leaf)
{
    /// <summary>Node id of the free-space area (free disk space, idle CPU, available memory).</summary>
    public const int Free = -2;

    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + W && y < Y + H;
}

/// <summary>Squarified treemap (Bruls, Huizing, van Wijk). Parents precede children in the result.</summary>
public static class Treemap
{
    const float MinSide = 3;

    /// <summary>
    /// Lays out the subtree. A positive <paramref name="free"/> adds a free-space area (Tile.Free)
    /// next to the root's children, sized like an item of that many bytes (or units).
    /// </summary>
    public static List<Tile> Layout(NodeIndex index, int root, float width, float height, long free = 0)
    {
        var tiles = new List<Tile>();
        Place(index, root, 0, 0, width, height, 0, tiles, Math.Max(0, free));
        return tiles;
    }

    static void Place(NodeIndex index, int node, float x, float y, float w, float h, int depth, List<Tile> tiles, long free = 0)
    {
        var children = index.Children(node);
        // (node, size), largest first; the free area takes its place among the children by size.
        var items = new (int Node, long Size)[children.Length + (free > 0 ? 1 : 0)];
        for (int c = 0; c < children.Length; c++) items[c] = (children[c], index.Total[children[c]]);
        if (free > 0)
        {
            items[^1] = (Tile.Free, free);
            Array.Sort(items, (a, b) => b.Size.CompareTo(a.Size));
        }
        // Scale by the children only, so that they fill the whole rectangle.
        long sum = 0;
        foreach (var item in items) sum += item.Size;
        bool leaf = sum <= 0 || w < 2 * MinSide || h < 2 * MinSide || depth > 200;
        tiles.Add(new Tile(node, x, y, w, h, depth, leaf));
        if (leaf) return;
        if (w > 12 && h > 12) { x++; y++; w -= 2; h -= 2; }
        double scale = (double)w * h / sum;

        for (int i = 0; i < items.Length;)
        {
            double side = Math.Min(w, h);
            if (side < 1) return;
            double row = 0, best = double.MaxValue;
            double largest = items[i].Size * scale;
            int end = i;
            while (end < items.Length)
            {
                double area = items[end].Size * scale;
                if (area <= 0) break;
                double next = row + area;
                double worst = Math.Max(side * side * largest / (next * next), next * next / (side * side * area));
                if (worst > best) break;
                best = worst;
                row = next;
                end++;
            }
            float thickness = (float)(row / side);
            if (end == i || thickness < 0.05f) return; // the rest is invisible
            bool vertical = w >= h;
            float offset = 0;
            for (; i < end; i++)
            {
                float extent = (float)(items[i].Size * scale / thickness);
                float cw = vertical ? thickness : extent, ch = vertical ? extent : thickness;
                float cx = vertical ? x : x + offset, cy = vertical ? y + offset : y;
                if (cw >= 1 && ch >= 1)
                {
                    if (items[i].Node == Tile.Free) tiles.Add(new Tile(Tile.Free, cx, cy, cw, ch, depth + 1, true));
                    else Place(index, items[i].Node, cx, cy, cw, ch, depth + 1, tiles);
                }
                offset += extent;
            }
            if (vertical) { x += thickness; w -= thickness; } else { y += thickness; h -= thickness; }
        }
    }
}
