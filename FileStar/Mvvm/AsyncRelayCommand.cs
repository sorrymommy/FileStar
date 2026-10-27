using System.Windows.Input;

namespace FileStar.Mvvm;

/// <summary>
/// A command for async work: it reports itself not executable while a run is in progress, so a double click
/// can't start the same operation twice.
/// </summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    bool _running;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
            return;
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute();
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
