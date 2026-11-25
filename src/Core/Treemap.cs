namespace GigaGlade;

public readonly record struct Tile(int Node, float X, float Y, float W, float H, int Depth, bool Leaf)
{
    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + W && y < Y + H;
}

/// <summary>Squarified treemap (Bruls, Huizing, van Wijk). Parents precede children in the result.</summary>
public static class Treemap
{
    const float MinSide = 3;

    public static List<Tile> Layout(NodeIndex index, int root, float width, float height)
    {
        var tiles = new List<Tile>();
        Place(index, root, 0, 0, width, height, 0, tiles);
        return tiles;
    }

    static void Place(NodeIndex index, int node, float x, float y, float w, float h, int depth, List<Tile> tiles)
    {
        var children = index.Children(node);
        // Scale by the children only, so that they fill the whole rectangle.
        long sum = 0;
        foreach (int c in children) sum += index.Total[c];
        bool leaf = sum <= 0 || w < 2 * MinSide || h < 2 * MinSide || depth > 200;
        tiles.Add(new Tile(node, x, y, w, h, depth, leaf));
        if (leaf) return;
        if (w > 12 && h > 12) { x++; y++; w -= 2; h -= 2; }
        double scale = (double)w * h / sum;

        for (int i = 0; i < children.Length;)
        {
            double side = Math.Min(w, h);
            if (side < 1) return;
            double row = 0, best = double.MaxValue;
            double largest = index.Total[children[i]] * scale;
            int end = i;
            while (end < children.Length)
            {
                double area = index.Total[children[end]] * scale;
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
                float extent = (float)(index.Total[children[i]] * scale / thickness);
                float cw = vertical ? thickness : extent, ch = vertical ? extent : thickness;
                if (cw >= 1 && ch >= 1)
                    Place(index, children[i], vertical ? x : x + offset, vertical ? y + offset : y, cw, ch, depth + 1, tiles);
                offset += extent;
            }
            if (vertical) { x += thickness; w -= thickness; } else { y += thickness; h -= thickness; }
        }
    }
}
