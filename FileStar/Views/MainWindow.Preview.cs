using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FileStar.Views;

/// <summary>The 미리보기 tab: images and text are rendered here; everything else goes through Windows preview handlers.</summary>
public partial class MainWindow
{
    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".heic", ".dds",
    };

    static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".tsv", ".json", ".xml", ".xsd", ".md", ".ini", ".cfg", ".conf", ".config", ".properties",
        ".yaml", ".yml", ".sql", ".cs", ".java", ".kt", ".js", ".ts", ".jsx", ".tsx", ".py", ".c", ".cpp", ".h", ".hpp",
        ".go", ".rs", ".php", ".rb", ".sh", ".bat", ".cmd", ".ps1", ".html", ".htm", ".css", ".scss", ".vue", ".svelte",
        ".gradle", ".toml", ".env", ".gitignore", ".editorconfig", ".csproj", ".sln", ".xaml", ".svg", ".gml", ".aixm",
    };

    const int TextPreviewBytes = 512 * 1024;
    const long SniffTextMaxSize = 5 * 1024 * 1024;
    const uint FILE_ATTRIBUTE_OFFLINE = 0x1000, FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    string? _previewPath;
    string? _cloudApprovedPath;
    int _previewVersion;

    /// <summary>Shows the selected file in the 미리보기 tab; skipped while the tab is hidden.</summary>
    void UpdatePreviewPane(bool force = false)
    {
        if (!_vm.IsPreviewTabVisible)
            return;
        var selected = _vm.InfoSelection;
        string? path = selected.Count == 1 ? selected[0].FullPath : null;
        if (!force && path != null && string.Equals(path, _previewPath, StringComparison.OrdinalIgnoreCase))
            return;   // already showing it (e.g. the list just refreshed)

        ClearPreview();
        _previewPath = path;
        int version = ++_previewVersion;

        if (selected.Count == 0)
        {
            ShowPreviewMessage("미리 볼 파일을 선택하세요.");
            return;
        }
        if (selected.Count > 1)
        {
            ShowPreviewMessage($"{selected.Count:N0}개 항목이 선택되었습니다.\n미리보기는 한 파일씩 볼 수 있습니다.");
            return;
        }
        var item = selected[0];
        if (item.IsDirectory)
        {
            ShowPreviewMessage("폴더는 미리보기가 없습니다.\n폴더를 열어 내용을 확인하세요.");
            return;
        }

        // Cloud-only files (OneDrive "열 때 다운로드") would be downloaded just by previewing; ask first.
        uint attributes = FileProperties.RawAttributes(path!);
        if ((attributes & (FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)) != 0 &&
            !string.Equals(_cloudApprovedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            ShowPreviewMessage("이 파일은 OneDrive(클라우드)에만 있습니다.\n미리 보려면 이 PC로 내려받아야 합니다.", showLoadButton: true);
            return;
        }

        string ext = item.RawExtension;
        if (ImageExtensions.Contains(ext))
            _ = PreviewImageAsync(path!, version);
        else if (TextExtensions.Contains(ext))
            _ = PreviewTextAsync(path!, version, sniff: false);
        else if (HancomPreview.Supports(ext))
            _ = PreviewHancomAsync(path!, version);
        else if (PreviewHost.FindHandler(ext) is Guid handler)
            PreviewWithHandler(path!, handler, version);
        else if ((item.Size ?? 0) <= SniffTextMaxSize)
            _ = PreviewTextAsync(path!, version, sniff: true);
        else
            ShowPreviewMessage($"'{ext}' 형식은 미리보기를 지원하지 않습니다.");
    }

    void ClearPreview()
    {
        _previewVersion++;
        _previewPath = null;
        PreviewHandlerHost.Unload();
        PreviewImage.Source = null;
        PreviewText.Text = "";
        PreviewDocImage.Source = null;
        PreviewDocText.Text = "";
        PreviewInfo.Text = "";
        ShowPreviewView(null);
    }

    /// <summary>Makes one preview surface visible (null = the message panel).</summary>
    void ShowPreviewView(UIElement? view)
    {
        PreviewImage.Visibility = view == PreviewImage ? Visibility.Visible : Visibility.Collapsed;
        PreviewText.Visibility = view == PreviewText ? Visibility.Visible : Visibility.Collapsed;
        PreviewHandlerHost.Visibility = view == PreviewHandlerHost ? Visibility.Visible : Visibility.Collapsed;
        PreviewDocView.Visibility = view == PreviewDocView ? Visibility.Visible : Visibility.Collapsed;
        PreviewMessagePanel.Visibility = view == null ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowPreviewMessage(string message, bool showLoadButton = false)
    {
        PreviewMessage.Text = message;
        PreviewLoadButton.Visibility = showLoadButton ? Visibility.Visible : Visibility.Collapsed;
        ShowPreviewView(null);
    }

    void PreviewLoad_Click(object sender, RoutedEventArgs e)
    {
        _cloudApprovedPath = _previewPath;
        UpdatePreviewPane(force: true);
    }

    async Task PreviewImageAsync(string path, int version)
    {
        ShowPreviewMessage("이미지를 불러오는 중...");
        try
        {
            // Decode off the UI thread, scaled down to roughly screen size, without locking the file.
            var (image, width, height) = await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                int w = frame.PixelWidth, h = frame.PixelHeight;
                stream.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                if (w > 1600)
                    bitmap.DecodePixelWidth = 1600;
                bitmap.EndInit();
                return (NormalizeDpi(bitmap), w, h);
            });
            if (version != _previewVersion)
                return;
            PreviewImage.Source = image;
            PreviewInfo.Text = $"{width:N0} × {height:N0} 픽셀";
            ShowPreviewView(PreviewImage);
        }
        catch (Exception ex)
        {
            if (version == _previewVersion)
                ShowPreviewMessage("이미지를 열 수 없습니다.\n" + ex.Message);
        }
    }

    /// <summary>
    /// Re-tags the bitmap as 96 DPI so it is sized by its pixels. WPF otherwise honors the file's DPI, and some
    /// writers store nonsense (Hancom previews claim ~115,000 DPI, which renders a full page under 1px wide).
    /// </summary>
    static BitmapSource NormalizeDpi(BitmapSource source)
    {
        if (Math.Abs(source.DpiX - 96) < 0.5 && Math.Abs(source.DpiY - 96) < 0.5)
        {
            source.Freeze();
            return source;
        }
        var converted = source.Format == System.Windows.Media.PixelFormats.Bgra32 ? source
            : new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    async Task PreviewTextAsync(string path, int version, bool sniff)
    {
        ShowPreviewMessage("불러오는 중...");
        try
        {
            var result = await Task.Run(() => ReadTextPreview(path));
            if (version != _previewVersion)
                return;
            if (result == null)
            {
                ShowPreviewMessage(sniff
                    ? $"'{Path.GetExtension(path)}' 형식은 미리보기를 지원하지 않습니다."
                    : "텍스트로 읽을 수 없는 파일입니다.");
                return;
            }
            var (text, encoding, truncated) = result.Value;
            PreviewText.Text = text;
            PreviewText.ScrollToHome();
            PreviewInfo.Text = $"텍스트 · {encoding}" + (truncated ? $" · 처음 {TextPreviewBytes / 1024:N0}KB만 표시" : "");
            ShowPreviewView(PreviewText);
        }
        catch (Exception ex)
        {
            if (version == _previewVersion)
                ShowPreviewMessage("파일을 읽을 수 없습니다.\n" + ex.Message);
        }
    }

    /// <summary>Reads the start of a file as text, guessing the encoding; null if it looks binary.</summary>
    static (string Text, string Encoding, bool Truncated)? ReadTextPreview(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[(int)Math.Min(TextPreviewBytes, stream.Length)];
        int length = 0;
        while (length < buffer.Length && stream.Read(buffer, length, buffer.Length - length) is int read && read > 0)
            length += read;
        bool truncated = stream.Length > length;

        if (length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
            return (Encoding.Unicode.GetString(buffer, 2, length - 2), "UTF-16", truncated);
        if (length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(buffer, 2, length - 2), "UTF-16 BE", truncated);
        if (length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            return (Encoding.UTF8.GetString(buffer, 3, length - 3), "UTF-8", truncated);

        // NUL bytes mean binary data (UTF-16 without BOM is rare enough to ignore).
        int sample = Math.Min(length, 8192);
        if (Enumerable.Range(0, sample).Count(i => buffer[i] == 0) > sample / 100)
            return null;

        var strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        // A cut at the 512KB boundary may split a multi-byte character; drop up to 3 trailing bytes.
        for (int cut = 0; cut <= (truncated ? 3 : 0); cut++)
        {
            try { return (strictUtf8.GetString(buffer, 0, length - cut), "UTF-8", truncated); }
            catch (DecoderFallbackException) { }
        }
        return (Encoding.GetEncoding(949).GetString(buffer, 0, length), "ANSI (CP949)", truncated);
    }

    async Task PreviewHancomAsync(string path, int version)
    {
        ShowPreviewMessage("한글 문서를 불러오는 중...");
        try
        {
            var (image, text) = await Task.Run(() =>
            {
                var (imageBytes, body) = HancomPreview.Read(path);
                BitmapSource? bitmap = null;
                if (imageBytes is { Length: > 0 })
                {
                    var decoded = new BitmapImage();
                    decoded.BeginInit();
                    decoded.CacheOption = BitmapCacheOption.OnLoad;
                    decoded.StreamSource = new MemoryStream(imageBytes);
                    decoded.EndInit();
                    bitmap = NormalizeDpi(decoded);
                }
                return (bitmap, body);
            });
            if (version != _previewVersion)
                return;
            if (image == null && string.IsNullOrWhiteSpace(text))
            {
                ShowPreviewMessage("이 한글 문서에는 미리보기 정보가 저장되어 있지 않습니다.");
                return;
            }
            PreviewDocImage.Source = image;
            PreviewDocText.Text = text?.Trim() ?? "";
            PreviewDocText.ScrollToHome();
            PreviewInfo.Text = "한글 문서에 저장된 미리보기 (첫 쪽 이미지 + 본문 텍스트)";
            ShowPreviewView(PreviewDocView);
        }
        catch (Exception ex)
        {
            if (version == _previewVersion)
                ShowPreviewMessage("한글 문서의 미리보기를 읽을 수 없습니다.\n" + ex.Message);
        }
    }

    void PreviewWithHandler(string path, Guid handler, int version)
    {
        ShowPreviewMessage("미리보기를 준비하는 중...");
        ShowPreviewView(PreviewHandlerHost);
        // Let the host window get its size first; handlers render into whatever rectangle they get at start.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (version != _previewVersion)
                return;
            try
            {
                PreviewHandlerHost.Open(path, handler);
                PreviewInfo.Text = "Windows 미리보기 처리기로 표시 (탐색기 미리보기와 동일)";
            }
            catch (Exception ex)
            {
                ShowPreviewMessage("이 파일의 미리보기를 표시할 수 없습니다.\n" + ex.Message);
            }
        });
    }
}
