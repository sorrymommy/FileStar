using System.IO;
using System.Runtime.InteropServices;

namespace FileStar.Platform;

/// <summary>
/// What Windows knows about the network: the computers Explorer's 네트워크 shows and the folders a computer shares.
/// Both go over the network and can take seconds: never call them on the UI thread.
/// </summary>
public static class NetworkShell
{
    static readonly Guid FOLDERID_NetworkFolder = new("D20BEEC4-5CA8-4905-AE3B-BF251EA09B53");
    static readonly Guid BHID_EnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");
    static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    static readonly Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    /// <summary>
    /// The computers in Explorer's 네트워크 ("\\NAME"), found by Windows' network discovery. Media servers and other
    /// devices without shared folders are left out. Empty if discovery is off or fails.
    /// </summary>
    public static List<string> FindComputers()
    {
        var result = new List<string>();
        // The shell's namespace objects are apartment-threaded.
        var thread = new Thread(() =>
        {
            var found = new List<string>();
            try
            {
                ListNetworkFolder(found);
            }
            // Its own thread: anything escaping it would end the process, and COM failures come as many exception types.
            catch (Exception ex)
            {
                Perf.Log("network discovery failed: " + ex.Message);
            }
            found.Sort(Native.StrCmpLogicalW);
            lock (result)
                result.AddRange(found);
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(DiscoveryTimeout))
            Perf.Log("network discovery timed out");   // left to finish on its own; its late answer is dropped
        lock (result)
            return result.ToList();
    }

    static void ListNetworkFolder(List<string> found)
    {
        var folderId = FOLDERID_NetworkFolder;
        var itemIid = IID_IShellItem;
        Marshal.ThrowExceptionForHR(SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref itemIid, out var networkObject));
        var network = (IShellItem)networkObject;
        object? enumObject = null;
        try
        {
            var handler = BHID_EnumItems;
            var enumIid = IID_IEnumShellItems;
            network.BindToHandler(IntPtr.Zero, ref handler, ref enumIid, out enumObject);
            var items = (IEnumShellItems)enumObject;
            while (items.Next(1, out var item, out uint fetched) == 0 && fetched == 1)
            {
                try
                {
                    // Computers parse as "\\NAME"; media devices and the like as "::{…}" shell IDs.
                    if (ParsingName(item)?.TrimEnd('\\') is { Length: > 2 } name && name.StartsWith(@"\\") && IsPlainName(name[2..]))
                        found.Add(name);
                }
                catch (Exception ex) when (ex is COMException or ArgumentException or NotImplementedException)
                {
                    Perf.Log("network item skipped: " + ex.Message);   // one odd device doesn't end the list
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            if (enumObject != null)
                Marshal.ReleaseComObject(enumObject);
            Marshal.ReleaseComObject(network);
        }
    }

    static string? ParsingName(IShellItem item)
    {
        item.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out var text);
        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    /// <summary>
    /// The folders <paramref name="server"/> ("\\NAME") shares, as "\\NAME\share", in Explorer's order; hidden admin
    /// shares (C$, ADMIN$) and printers are left out. Null if the computer can't be asked.
    /// </summary>
    public static List<string>? DiskShares(string server)
    {
        int resume = 0;
        int result = NetShareEnum(server, 1, out var buffer, MAX_PREFERRED_LENGTH, out int count, out _, ref resume);
        try
        {
            if (result is not (NERR_Success or ERROR_MORE_DATA))
                return null;
            var shares = new List<string>(count);
            int size = Marshal.SizeOf<SHARE_INFO_1>();
            for (int i = 0; i < count; i++)
            {
                var share = Marshal.PtrToStructure<SHARE_INFO_1>(buffer + i * size);
                // The names come from the other computer: only a plain name makes a path to that share and nowhere else.
                if ((share.Type & STYPE_MASK) == STYPE_DISKTREE && (share.Type & STYPE_SPECIAL) == 0 &&
                    share.Name is { } name && IsPlainName(name) && !name.EndsWith('$'))
                    shares.Add(server.TrimEnd('\\') + "\\" + name);
            }
            shares.Sort(Native.StrCmpLogicalW);
            return shares;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                NetApiBufferFree(buffer);
        }
    }

    /// <summary>A single path segment: no separators, control or other characters Windows forbids in names, not "." or "..".</summary>
    static bool IsPlainName(string name) =>
        name.Trim() is { Length: > 0 } trimmed && trimmed is not ("." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(30);
    const int NERR_Success = 0, ERROR_MORE_DATA = 234, MAX_PREFERRED_LENGTH = -1;
    const uint STYPE_MASK = 0xFF, STYPE_DISKTREE = 0, STYPE_SPECIAL = 0x80000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHARE_INFO_1
    {
        public string Name;
        public uint Type;
        public string Remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetShareEnum(string serverName, int level, out IntPtr buffer, int preferredMaxLength,
        out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("netapi32.dll")]
    static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderItem(ref Guid folderId, uint flags, IntPtr token, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object item);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint count, out IShellItem item, out uint fetched);
        void Skip(uint count);
        void Reset();
        void Clone(out IEnumShellItems copy);
    }
}
