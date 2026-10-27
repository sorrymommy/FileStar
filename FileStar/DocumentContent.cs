using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace FileStar;

/// <summary>What gets sent to the AI for one file: extracted text, or the raw PDF / image for models that read them.</summary>
public sealed class DocumentContent
{
    public required string FileName { get; init; }
    public string? Text { get; init; }
    public byte[]? Pdf { get; init; }
    public byte[]? Image { get; init; }
    public string ImageMediaType { get; init; } = "image/jpeg";

    /// <summary>Shown to the user, e.g. "앞부분 120,000자만 분석" or "한글 미리보기 텍스트(일부)만 분석".</summary>
    public string? Note { get; init; }
}

/// <summary>Pulls the readable content out of a file without the application that made it.</summary>
public static class DocumentReader
{
    const int MaxPdfBytes = 30 * 1024 * 1024;   // request limit is 32MB including base64 overhead headroom for text

    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".tif", ".tiff", ".webp",
    };

    // Formats this reader understands beyond images and PDF, and the common text formats worth summarizing.
    static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".xlsm", ".pptx", ".hwp", ".hwpx",
        ".txt", ".md", ".csv", ".tsv", ".log", ".json", ".xml", ".html", ".htm", ".yaml", ".yml", ".ini", ".cfg", ".conf",
        ".sql", ".cs", ".java", ".py", ".js", ".ts", ".c", ".cpp", ".h", ".css", ".jsp", ".php", ".sh", ".bat", ".ps1", ".properties",
    };

    /// <summary>Worth sending to the AI when a whole folder is picked (skips programs, archives, old .doc/.xls …).</summary>
    public static bool IsReadable(string path) =>
        DocumentExtensions.Contains(Path.GetExtension(path)) || ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
    public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <param name="maxChars">Text beyond this is cut, and <see cref="DocumentContent.Note"/> says so.</param>
    public static DocumentContent Read(string path, int maxChars)
    {
        string name = Path.GetFileName(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".pdf")
        {
            long size = new FileInfo(path).Length;
            if (size > MaxPdfBytes)
                throw new InvalidOperationException($"PDF가 너무 큽니다 ({FileSystem.FormatBytes(size)}). 30MB 이하만 보낼 수 있습니다.");
            return new DocumentContent { FileName = name, Pdf = File.ReadAllBytes(path) };
        }
        if (ImageExtensions.Contains(ext))
            return new DocumentContent { FileName = name, Image = ShrinkImage(path), ImageMediaType = "image/jpeg" };

        string? note = null;
        string? text = ext switch
        {
            ".docx" => ReadDocx(path),
            ".xlsx" or ".xlsm" => ReadXlsx(path),
            ".pptx" => ReadPptx(path),
            ".hwpx" => ReadHwpx(path),
            ".hwp" => ReadHwp(path, out note),
            _ => ReadPlainText(path),
        };
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("파일에서 읽을 수 있는 텍스트를 찾지 못했습니다.");

        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        if (text.Length > maxChars)
        {
            note = (note == null ? "" : note + " · ") + $"전체 {text.Length:N0}자 중 앞부분 {maxChars:N0}자만 분석";
            text = text[..maxChars];
        }
        return new DocumentContent { FileName = name, Text = text, Note = note };
    }

    // ───────────── Office Open XML ─────────────

    static ZipArchive OpenZip(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), ZipArchiveMode.Read);

    static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    /// <summary>
    /// One line per paragraph. Each text run belongs to its nearest enclosing paragraph, so paragraphs nested in
    /// tables or text boxes (HWPX puts whole tables inside a paragraph) come out as their own lines instead of
    /// being glued onto the outer one.
    /// </summary>
    static string ParagraphText(XDocument doc, string paragraphName, string textName)
    {
        var sb = new StringBuilder();
        foreach (var paragraph in doc.Descendants().Where(e => e.Name.LocalName == paragraphName))
        {
            var line = string.Concat(paragraph.Descendants()
                .Where(e => e.Name.LocalName == textName &&
                            e.Ancestors().First(a => a.Name.LocalName == paragraphName) == paragraph)
                .Select(e => e.Value));
            line = PrivateUseChars.Replace(line, "").Trim();
            if (line.Length > 0)
                sb.AppendLine(line);
        }
        return sb.ToString();
    }

    // Hancom stores its bullet/number glyphs in Unicode private-use areas; they mean nothing outside Hancom fonts.
    static readonly Regex PrivateUseChars = new(@"[-]|[\uDB80-\uDBFF][\uDC00-\uDFFF]");

    static string ReadDocx(string path)
    {
        using var zip = OpenZip(path);
        var body = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("word/document.xml 없음");
        return ParagraphText(LoadXml(body), "p", "t");
    }

    static string ReadPptx(string path)
    {
        using var zip = OpenZip(path);
        var slides = zip.Entries
            .Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$"))
            .OrderBy(e => int.Parse(Regex.Match(e.FullName, @"\d+").Value));
        var sb = new StringBuilder();
        foreach (var slide in slides)
        {
            sb.AppendLine($"[슬라이드 {Regex.Match(slide.FullName, @"\d+").Value}]");
            sb.AppendLine(ParagraphText(LoadXml(slide), "p", "t"));
        }
        return sb.ToString();
    }

    static string ReadXlsx(string path)
    {
        using var zip = OpenZip(path);
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sharedEntry)
        {
            foreach (var si in LoadXml(sharedEntry).Root!.Elements().Where(e => e.Name.LocalName == "si"))
                shared.Add(string.Concat(si.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value)));
        }

        // Sheet names in workbook order, mapped to their part files through the relationships.
        var workbook = LoadXml(zip.GetEntry("xl/workbook.xml")!);
        var rels = LoadXml(zip.GetEntry("xl/_rels/workbook.xml.rels")!).Root!.Elements()
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);
        var sb = new StringBuilder();
        foreach (var sheet in workbook.Descendants().Where(e => e.Name.LocalName == "sheet"))
        {
            string? relId = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
            if (relId == null || !rels.TryGetValue(relId, out var target))
                continue;
            string partName = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (zip.GetEntry(partName) is not { } sheetEntry)
                continue;

            sb.AppendLine($"[시트: {(string?)sheet.Attribute("name")}]");
            foreach (var row in LoadXml(sheetEntry).Descendants().Where(e => e.Name.LocalName == "row"))
            {
                var cells = row.Elements().Where(e => e.Name.LocalName == "c").Select(c =>
                {
                    string type = (string?)c.Attribute("t") ?? "";
                    var v = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
                    if (type == "s" && int.TryParse(v, out int index) && index < shared.Count)
                        return shared[index];
                    if (type == "inlineStr")
                        return string.Concat(c.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                    return v ?? "";
                }).Where(v => v.Length > 0).ToList();
                if (cells.Count > 0)
                    sb.AppendLine(string.Join(" | ", cells));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ───────────── Hancom ─────────────

    static string ReadHwpx(string path)
    {
        using var zip = OpenZip(path);
        var sections = zip.Entries
            .Where(e => Regex.IsMatch(e.FullName, @"^Contents/section\d+\.xml$", RegexOptions.IgnoreCase))
            .OrderBy(e => int.Parse(Regex.Match(e.FullName, @"\d+").Value));
        var sb = new StringBuilder();
        foreach (var section in sections)
            sb.AppendLine(ParagraphText(LoadXml(section), "p", "t"));
        return sb.ToString();
    }

    /// <summary>
    /// HWP 5 body text is compressed binary records; the embedded preview text (the first part of the
    /// document) is what is reliably readable without Hancom's libraries.
    /// </summary>
    static string? ReadHwp(string path, out string? note)
    {
        note = "HWP는 문서에 저장된 미리보기 텍스트(앞부분)만 분석 — 전체 분석은 HWPX로 저장 후 실행";
        return HancomPreview.Read(path).Text;
    }

    // ───────────── Plain text & images ─────────────

    static string? ReadPlainText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 20 * 1024 * 1024)
            throw new InvalidOperationException("지원하지 않는 형식이거나 너무 큰 파일입니다.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        int sample = Math.Min(bytes.Length, 8192);
        if (bytes.Take(sample).Count(b => b == 0) > sample / 100)
            throw new InvalidOperationException($"'{Path.GetExtension(path)}' 형식은 내용을 읽을 수 없습니다.");
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(949).GetString(bytes);
        }
    }

    /// <summary>Scales to at most 1568px on the long side (larger gains nothing) and re-encodes as JPEG.</summary>
    static byte[] ShrinkImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        double scale = Math.Min(1.0, 1568.0 / Math.Max(frame.PixelWidth, frame.PixelHeight));
        BitmapSource source = scale < 1 ? new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale)) : frame;

        // JPEG has no alpha. Dropping it would turn transparent areas black (hiding dark diagram lines),
        // so blend onto white first.
        var bgra = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int alpha = pixels[i + 3];
            for (int c = 0; c < 3; c++)
                pixels[i + c] = (byte)((pixels[i + c] * alpha + 255 * (255 - alpha)) / 255);
            pixels[i + 3] = 255;
        }
        var flattened = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);

        var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(flattened));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
