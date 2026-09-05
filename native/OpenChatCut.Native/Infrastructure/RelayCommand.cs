using System.Windows.Input;

namespace OpenChatCut.Native.Infrastructure;

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    private readonly Action<object?> _execute = execute;
    private readonly Predicate<object?>? _canExecute = canExecute;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Func<Task> _execute = execute;
    private readonly Func<bool>? _canExecute = canExecute;
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute();
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand<T>(Func<T, Task> execute, Func<T, bool>? canExecute = null) : ICommand
{
    private readonly Func<T, Task> _execute = execute;
    private readonly Func<T, bool>? _canExecute = canExecute;
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return !_running && parameter is T value && (_canExecute?.Invoke(value) ?? true);
    }

    public async void Execute(object? parameter)
    {
        if (parameter is not T value || !CanExecute(parameter)) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute(value);
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
