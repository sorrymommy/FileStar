using System.Diagnostics;
using System.IO;

namespace FileStar.Platform;

/// <summary>
/// Timing log for finding slow spots, plus WPF binding errors. Off unless the environment variable
/// FILESTAR_TRACE=1 is set; then lines go to %TEMP%\FileStar-trace.log.
/// </summary>
internal static class Perf
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("FILESTAR_TRACE") == "1";
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "FileStar-trace.log");
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static long Now => Clock.ElapsedMilliseconds;

    public static void Log(string message)
    {
        if (!Enabled)
            return;
        try { File.AppendAllText(LogPath, $"{Now,8} ms  {message}{Environment.NewLine}"); }
        catch { }
    }

    /// <summary>Sends WPF's binding warnings (a misspelled path, a wrong type) to the log too.</summary>
    public static void TraceBindings()
    {
        if (!Enabled)
            return;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new LogListener());
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
    }

    sealed class LogListener : TraceListener
    {
        public override void Write(string? message) { }

        public override void WriteLine(string? message) => Log("binding: " + message);
    }
}
