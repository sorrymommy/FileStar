using System.IO;
using System.Windows;

namespace FileStar.Views;

/// <summary>What dropping files on a folder does, the same in every list and the tree.</summary>
public static class DropRules
{
    /// <summary>Explorer's rule: Ctrl copies, Shift moves, otherwise move within a drive and copy across drives.</summary>
    public static DragDropEffects EffectFor(DragEventArgs e, string[] files, string folder)
    {
        DragDropEffects wanted = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) ? DragDropEffects.Copy
            : e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey) ? DragDropEffects.Move
            : files.All(f => string.Equals(Path.GetPathRoot(f), Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase)) ? DragDropEffects.Move
            : DragDropEffects.Copy;
        return e.AllowedEffects.HasFlag(wanted) ? wanted : e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>Not into itself or one of its own subfolders.</summary>
    public static bool IsInsideDragged(string[] files, string folder) => files.Any(f => Locations.IsSameOrUnder(folder, f));

    /// <summary>The effect to show while dragging over <paramref name="folder"/>: none for a move into the folder it is already in.</summary>
    public static DragDropEffects PreviewEffect(DragEventArgs e, string[] files, string folder)
    {
        if (IsInsideDragged(files, folder))
            return DragDropEffects.None;
        var effect = EffectFor(e, files, folder);
        return effect == DragDropEffects.Move && Locations.AllDirectlyIn(files, folder) ? DragDropEffects.None : effect;
    }

    public static string[]? DraggedFiles(DragEventArgs e) => e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files ? files : null;
}
