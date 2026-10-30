using System.IO;

namespace FileStar.Services;

public static class FileNames
{
    public const string InvalidCharsMessage = "이름에 사용할 수 없는 문자가 있습니다: \\ / : * ? \" < > |";

    public static bool HasInvalidChars(string name) => name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;

    /// <summary>
    /// Throws unless <paramref name="name"/> names an item directly in a folder: no separators or other forbidden
    /// characters, and not "." or "..", which would rename or create something outside it.
    /// </summary>
    public static void EnsurePlainName(string name)
    {
        if (name.Trim().Length == 0 || name.Trim() is "." or ".." || HasInvalidChars(name))
            throw new ArgumentException(InvalidCharsMessage, nameof(name));
    }

    /// <summary>The part of a name to preselect for renaming, as Explorer does: everything but the extension.</summary>
    public static int StemLength(string name, string extension, bool isDirectory) =>
        isDirectory || extension.Length == 0 ? name.Length : Math.Max(0, name.Length - extension.Length);
}
