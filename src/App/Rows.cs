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

/// <summary>
/// Files: colours for the file types that use the most space, everything else grey.
/// Programs: every program always gets the same colour, derived from its name.
/// </summary>
public sealed class TypeColors
{
    static readonly uint[] Palette =
        [0x4C78A8, 0xF58518, 0x54A24B, 0xE45756, 0x72B7B2, 0xEECA3B, 0xB279A2, 0xFF9DA6, 0x9D755D, 0x6B8E23, 0x8E6BD6, 0x2E8B9A,
         0xC44E52, 0x55A868, 0xDD8452, 0x64B5CD];

    readonly Dictionary<string, uint> _colors;
    readonly bool _byName;

    public TypeColors(IEnumerable<TypeStat> types)
    {
        _colors = types.Where(t => t.Extension != "").Take(12).Select((t, i) => (t.Extension, Palette[i])).ToDictionary();
    }

    TypeColors() { _colors = []; _byName = true; }

    public static TypeColors ForPrograms() => new();

    public uint Get(string key)
    {
        if (!_byName) return _colors.GetValueOrDefault(key, 0x8A8A8Au);
        if (key.StartsWith('(')) return 0x4A4A4A; // (idle), (available), (kernel, ...): not a program
        // FNV-1a, because string.GetHashCode differs between runs.
        uint hash = 2166136261;
        foreach (char c in key.ToLowerInvariant()) hash = (hash ^ c) * 16777619;
        return Palette[hash % (uint)Palette.Length];
    }

    public SolidColorBrush Brush(string extension)
    {
        uint c = Get(extension);
        return new(Color.FromArgb(255, (byte)(c >> 16), (byte)(c >> 8), (byte)c));
    }
}
