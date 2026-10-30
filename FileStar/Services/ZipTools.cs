using System.IO;
using System.IO.Compression;
using System.Text;

namespace FileStar.Services;

/// <summary>zip / unzip with Windows' own conventions, run off the UI thread by the caller.</summary>
public static class ZipTools
{
    /// <summary>"이름.zip" in <paramref name="folder"/>, or "이름 (2).zip" … if taken.</summary>
    public static string FreeName(string folder, string baseName, string extension)
    {
        string path = Path.Combine(folder, baseName + extension);
        for (int n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(folder, $"{baseName} ({n}){extension}");
        return path;
    }

    /// <summary>Packs files and whole folders (with everything inside) into a new zip; names are stored as UTF-8.</summary>
    public static void Create(string zipPath, IReadOnlyList<string> items, CancellationToken token)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create, Encoding.UTF8);
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(item))
                zip.CreateEntryFromFile(item, Path.GetFileName(item), CompressionLevel.Optimal);
            else if (Directory.Exists(item))
            {
                string root = Path.GetDirectoryName(item.TrimEnd('\\'))!;
                var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = 0 };
                foreach (var file in Directory.EnumerateFiles(item, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Optimal);
                }
                // Keep empty folders too.
                foreach (var dir in Directory.EnumerateDirectories(item, "*", options).Prepend(item))
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        zip.CreateEntry(Path.GetRelativePath(root, dir).Replace('\\', '/') + "/");
                }
            }
        }
    }

    /// <summary>
    /// Unpacks into a new folder. Entry names are UTF-8 in modern zips (FileStar's own included) but CP949 in
    /// zips made by Korean Windows' "압축(zip) 폴더", so each name is read as UTF-8 if it is valid UTF-8, else as CP949.
    /// </summary>
    public static void Extract(string zipPath, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);
        ZipFile.ExtractToDirectory(zipPath, targetFolder, new Utf8OrCp949(), overwriteFiles: false);
        CopyMarkOfTheWeb(zipPath, targetFolder);
    }

    /// <summary>
    /// A zip downloaded from the internet carries a Zone.Identifier stream ("Mark of the Web"). Windows' own
    /// extraction copies it onto every extracted file, which is what makes SmartScreen, Office Protected View and
    /// macro blocking treat them as downloaded too; without it, a downloaded .docm or .exe inside would run as if
    /// it were local.
    /// </summary>
    static void CopyMarkOfTheWeb(string zipPath, string targetFolder)
    {
        string zone;
        try
        {
            zone = File.ReadAllText(zipPath + ":Zone.Identifier");
        }
        catch
        {
            return;   // not downloaded (or a file system without alternate streams)
        }
        foreach (var file in Directory.EnumerateFiles(targetFolder, "*", SearchOption.AllDirectories))
        {
            try { File.WriteAllText(file + ":Zone.Identifier", zone); }
            catch { }
        }
    }

    /// <summary>Decodes with strict UTF-8 and falls back to CP949 for byte sequences that aren't UTF-8 (reading only).</summary>
    sealed class Utf8OrCp949 : Encoding
    {
        static readonly Encoding Utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        static readonly Encoding Cp949 = CodePagesEncodingProvider.Instance.GetEncoding(949)!;

        static Encoding For(byte[] bytes, int index, int count)
        {
            try
            {
                Utf8.GetCharCount(bytes, index, count);
                return Utf8;
            }
            catch (DecoderFallbackException)
            {
                return Cp949;
            }
        }

        public override int GetByteCount(char[] chars, int index, int count) => UTF8.GetByteCount(chars, index, count);
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
            UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        public override int GetCharCount(byte[] bytes, int index, int count) => For(bytes, index, count).GetCharCount(bytes, index, count);
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
            For(bytes, byteIndex, byteCount).GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        public override string GetString(byte[] bytes, int index, int count) => For(bytes, index, count).GetString(bytes, index, count);
        public override int GetMaxByteCount(int charCount) => UTF8.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => Math.Max(UTF8.GetMaxCharCount(byteCount), Cp949.GetMaxCharCount(byteCount));
    }
}
