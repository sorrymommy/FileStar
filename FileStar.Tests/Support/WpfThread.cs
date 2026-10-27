using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace FileStar.Tests.Support;

/// <summary>
/// One STA thread with a running WPF dispatcher and an <see cref="Application"/> carrying FileStar's themes, shared
/// by every test that needs WPF (collection views, windows). Work runs there one piece at a time, with the
/// dispatcher's SynchronizationContext, so async ViewModel code resumes on it just as in the app.
/// </summary>
public static class WpfThread
{
    static readonly Lazy<Dispatcher> Instance = new(Start);
    static readonly List<string> BindingErrors = new();

    static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var theme in new[] { "Themes/Light.xaml", "Themes/Controls.xaml" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/FileStar;component/{theme}") });
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new Collector());
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF tests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.Result;
    }

    /// <summary>Runs <paramref name="test"/> on the WPF thread and waits for it, rethrowing its failure here.</summary>
    public static void Run(Func<Task> test) =>
        Instance.Value.InvokeAsync(test).Task.Unwrap().GetAwaiter().GetResult();

    public static T Run<T>(Func<Task<T>> test) =>
        Instance.Value.InvokeAsync(test).Task.Unwrap().GetAwaiter().GetResult();

    /// <summary>Lets queued work (layout, bindings, posted continuations) run before continuing.</summary>
    public static async Task Settle(int milliseconds = 50)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(milliseconds);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Binding errors reported since the last call.</summary>
    public static List<string> TakeBindingErrors()
    {
        lock (BindingErrors)
        {
            var errors = BindingErrors.ToList();
            BindingErrors.Clear();
            return errors;
        }
    }

    sealed class Collector : TraceListener
    {
        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            lock (BindingErrors)
                BindingErrors.Add(message ?? "");
        }
    }
}
