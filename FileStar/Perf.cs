using System.Diagnostics;
using System.IO;

namespace FileStar;

/// <summary>
/// Timing log for finding slow spots. Off unless the environment variable FILESTAR_TRACE=1 is set;
/// then lines go to %TEMP%\FileStar-trace.log.
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
}
