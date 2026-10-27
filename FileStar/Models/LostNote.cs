using System.IO;

namespace FileStar.Models;

/// <summary>A memo whose file is no longer at its path: found elsewhere by its file ID, or not found at all.</summary>
public sealed record LostNote(string OldPath, FileNote Note, string? NewPath)
{
    /// <summary>Same file name at the new place: a plain move, safe to relink without asking.</summary>
    public bool SameName => NewPath != null &&
        string.Equals(Path.GetFileName(OldPath), Path.GetFileName(NewPath), StringComparison.OrdinalIgnoreCase);
}

public sealed record NoteScanResult(List<LostNote> Lost, Dictionary<string, string> NewIds);
