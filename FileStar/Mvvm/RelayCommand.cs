using System.Windows.Input;

namespace FileStar.Mvvm;

/// <summary>An <see cref="ICommand"/> that runs a delegate; WPF re-asks <see cref="CanExecute"/> whenever input changes.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
            execute();
    }
}

/// <summary>A <see cref="RelayCommand"/> whose delegate takes the command parameter.</summary>
public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => parameter is T value ? canExecute?.Invoke(value) ?? true : false;

    public void Execute(object? parameter)
    {
        if (parameter is T value && CanExecute(value))
            execute(value);
    }
}
