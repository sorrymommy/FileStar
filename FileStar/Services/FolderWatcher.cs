using System.IO;

namespace FileStar.Services;

/// <summary>Watches folders for changes made by any program.</summary>
public interface IFolderWatchService
{
    /// <summary>
    /// Calls <paramref name="changed"/> once things settle after a burst of changes, and <paramref name="renamed"/>
    /// for every rename, both on the calling thread. Dispose to stop. Locations that can't be watched (some
    /// network shares) return a watcher that never fires.
    /// </summary>
    IDisposable Watch(string folder, Action changed, Action<string, string>? renamed = null);
}

public sealed class FolderWatchService : IFolderWatchService
{
    static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    public IDisposable Watch(string folder, Action changed, Action<string, string>? renamed = null) =>
        new Watcher(folder, changed, renamed);

    sealed class Watcher : IDisposable
    {
        readonly SynchronizationContext _context = SynchronizationContext.Current ?? new SynchronizationContext();
        readonly FileSystemWatcher? _watcher;
        readonly Timer _settle;
        readonly Action _changed;
        volatile bool _disposed;

        public Watcher(string folder, Action changed, Action<string, string>? renamed)
        {
            _changed = changed;
            _settle = new Timer(_ => Post(() => _changed()));
            try
            {
                _watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size |
                                   NotifyFilters.LastWrite | NotifyFilters.Attributes,
                };
                _watcher.Created += OnChange;
                _watcher.Deleted += OnChange;
                _watcher.Changed += OnChange;
                _watcher.Renamed += OnChange;
                _watcher.Error += OnChange;   // too many changes at once: individual events were lost, so reload
                if (renamed != null)
                    _watcher.Renamed += (_, e) => Post(() => renamed(e.OldFullPath, e.FullPath));
                _watcher.EnableRaisingEvents = true;
            }
            catch
            {
                _watcher = null;
            }
        }

        // Runs on a thread-pool thread, so it can race Dispose.
        void OnChange(object? sender, EventArgs e)
        {
            if (_disposed)
                return;
            try { _settle.Change(Settle, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }

        void Post(Action action) => _context.Post(_ =>
        {
            if (!_disposed)
                action();
        }, null);

        public void Dispose()
        {
            _disposed = true;
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
            _settle.Dispose();
        }
    }
}
