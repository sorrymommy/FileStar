using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FileStar;

/// <summary>
/// NTFS keeps a number for every file and folder that stays the same when it is moved or renamed on the same
/// drive. Stored next to a memo, it lets FileStar find the file again after another program (e.g. Windows
/// Explorer) moved it. A copy is a new file with a new number, and moves to another drive can't be followed.
/// </summary>
public static class FileIdentity
{
    /// <summary>"volume serial-file index" for an existing file or folder, or null (not NTFS, gone, no access).</summary>
    public static string? Get(string path)
    {
        try
        {
            using var handle = CreateFile(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
                return null;
            ulong index = ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow;
            return index == 0 ? null : $"{info.dwVolumeSerialNumber:X8}-{index:X16}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Where the file with this ID is now, or null if it no longer exists (or its drive isn't connected).
    /// Files sitting in the Recycle Bin count as gone.
    /// </summary>
    public static string? Resolve(string id)
    {
        var parts = id.Split('-');
        if (parts.Length != 2 || !uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out uint serial) ||
            !ulong.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out ulong index))
            return null;
        if (DriveFor(serial) is not string root)
            return null;
        try
        {
            using var volume = CreateFile(root, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (volume.IsInvalid)
                return null;
            var descriptor = new FILE_ID_DESCRIPTOR { dwSize = Marshal.SizeOf<FILE_ID_DESCRIPTOR>(), Type = 0, FileId = (long)index };
            using var handle = OpenFileById(volume, ref descriptor, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
            if (handle.IsInvalid)
                return null;
            var buffer = new StringBuilder(1024);
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
                return null;
            string path = buffer.ToString();
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                path = @"\\" + path[8..];
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                path = path[4..];
            return path.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    static Dictionary<uint, string>? _drives;

    /// <summary>The drive root with this volume serial number (local fixed and removable drives).</summary>
    static string? DriveFor(uint serial)
    {
        lock (typeof(FileIdentity))
        {
            if (_drives == null || !_drives.ContainsKey(serial))
            {
                // Rebuilt when a serial isn't known: a USB drive may have been plugged in since.
                var drives = new Dictionary<uint, string>();
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable))
                {
                    if (GetVolumeInformation(drive.RootDirectory.FullName, null, 0, out uint s, out _, out _, null, 0))
                        drives[s] = drive.RootDirectory.FullName;
                }
                _drives = drives;
            }
            return _drives.GetValueOrDefault(serial);
        }
    }

    const uint FILE_READ_ATTRIBUTES = 0x80, FILE_SHARE_ALL = 0x7, OPEN_EXISTING = 3, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint dwVolumeSerialNumber, nFileSizeHigh, nFileSizeLow, nNumberOfLinks, nFileIndexHigh, nFileIndexLow;
    }

    // FILE_ID_DESCRIPTOR: size, type (0 = 64-bit file ID), then a 16-byte union aligned to 8.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct FILE_ID_DESCRIPTOR
    {
        [FieldOffset(0)] public int dwSize;
        [FieldOffset(4)] public int Type;
        [FieldOffset(8)] public long FileId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FILE_ID_DESCRIPTOR id, uint access, uint share, IntPtr security, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformation(string root, StringBuilder? name, int nameSize, out uint serial,
        out uint maxComponent, out uint flags, StringBuilder? fsName, int fsNameSize);
}
