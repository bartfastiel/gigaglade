using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GigaGlade;

/// <summary>One visible line of the tree. Node is -1 for the "more" line.</summary>
public sealed record TreeRow(int Node, int Depth, string Name, string Size, double Percent, string Chevron)
{
    // Get-only, because the XAML compiler cannot handle init accessors.
    public int Node { get; } = Node;
    public int Depth { get; } = Depth;
    public string Name { get; } = Name;
    public string Size { get; } = Size;
    public double Percent { get; } = Percent;
    public string Chevron { get; } = Chevron;
    public Thickness Indent => new(Math.Min(Depth, 25) * 14, 0, 0, 0);
    public string Label => $"{Name}, {Size}";
    public override string ToString() => Label;
}

public sealed record ResultRow(int Node, string Title, string Detail, string Size, Brush? Brush = null)
{
    public int Node { get; } = Node;
    public string Title { get; } = Title;
    public string Detail { get; } = Detail;
    public string Size { get; } = Size;
    public Brush? Brush { get; } = Brush;
    public override string ToString() => Title;
}

public sealed record Crumb(int Node, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Colours for the file types that use the most space; everything else is grey.</summary>
public sealed class TypeColors(IEnumerable<TypeStat> types)
{
    static readonly uint[] Palette =
        [0x4C78A8, 0xF58518, 0x54A24B, 0xE45756, 0x72B7B2, 0xEECA3B, 0xB279A2, 0xFF9DA6, 0x9D755D, 0x6B8E23, 0x8E6BD6, 0x2E8B9A];

    readonly Dictionary<string, uint> _colors = types.Where(t => t.Extension != "").Take(Palette.Length)
        .Select((t, i) => (t.Extension, Palette[i])).ToDictionary();

    public uint Get(string extension) => _colors.GetValueOrDefault(extension, 0x8A8A8Au);

    public SolidColorBrush Brush(string extension)
    {
        uint c = Get(extension);
        return new(Color.FromArgb(255, (byte)(c >> 16), (byte)(c >> 8), (byte)c));
    }
}
