using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GigaGlade;

public enum DeleteRisk { Normal, System, Forbidden }

public static class Deletion
{
    static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    static readonly string[] Protected =
    [
        Windows,
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    ];

    static readonly string[] SystemNames =
        ["$Recycle.Bin", "System Volume Information", "Recovery", "Boot", "EFI", "bootmgr", "pagefile.sys", "swapfile.sys", "hiberfil.sys"];

    public static DeleteRisk Classify(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.GetPathRoot(full)!;
        if (full.Length <= root.Length) return DeleteRisk.Forbidden;
        string relative = full[root.Length..];
        string firstPart = relative.Split('\\')[0];
        bool topLevel = firstPart.Length == relative.Length;
        // The Windows folder itself, anything containing it, and NTFS metafiles ($MFT, $LogFile, ...).
        if (Same(full, Windows) || Inside(Windows, full)) return DeleteRisk.Forbidden;
        if (firstPart.StartsWith('$') && !Same(firstPart, "$Recycle.Bin")) return DeleteRisk.Forbidden;
        if (Protected.Any(p => Same(full, p) || Inside(full, p) || Inside(p, full))) return DeleteRisk.System;
        if (SystemNames.Any(s => Same(firstPart, s))) return DeleteRisk.System;
        // The folder of all profiles, or a whole profile.
        string users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "";
        if (Same(full, users) || Same(Path.GetDirectoryName(full) ?? "", users)) return DeleteRisk.System;
        return topLevel && Same(relative, "Users") ? DeleteRisk.System : DeleteRisk.Normal;
    }

    static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    static bool Inside(string path, string parent) => path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Moves to the recycle bin. If Windows would have to delete permanently instead,
    /// it asks the user. Returns false if the user cancelled.
    /// </summary>
    public static bool Recycle(string path, nint owner)
    {
        Guard(path);
        if (path.Length >= 260) throw new PathTooLongException("The path is too long for the recycle bin.");
        var operation = new ShFileOperation
        {
            Owner = owner,
            Function = 3, // FO_DELETE
            From = path + "\0\0",
            Flags = 0x0040 | 0x0010 | 0x4000, // ALLOWUNDO | NOCONFIRMATION | WANTNUKEWARNING
        };
        int result = SHFileOperationW(ref operation);
        if (operation.Aborted || result == 1223) return false;
        if (result != 0) throw new Win32Exception(result, $"Windows could not recycle the item (code 0x{result:X}).");
        return true;
    }

    public static void Permanently(string path)
    {
        Guard(path);
        // Directory.Delete removes links and junctions without following them.
        if (Directory.Exists(path)) Directory.Delete(path, true);
        else File.Delete(path);
    }

    static void Guard(string path)
    {
        if (Classify(path) == DeleteRisk.Forbidden) throw new InvalidOperationException("This item must not be deleted.");
        if (!Path.Exists(path)) throw new FileNotFoundException("The item no longer exists.", path);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOperation
    {
        public nint Owner;
        public uint Function;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public nint NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Title;
    }

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    static extern int SHFileOperationW(ref ShFileOperation operation);
}
