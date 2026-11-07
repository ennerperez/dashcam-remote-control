using System.Windows.Input;
using Avalonia.Controls;

namespace Dashcam.Remote.Player
{
    internal sealed class PlayerRelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => Design.IsDesignMode || (canExecute?.Invoke(parameter) ?? true);

        public void Execute(object? parameter)
        {
            if (!Design.IsDesignMode)
            {
                execute(parameter);
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    internal sealed class PlayerAsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
    {
        private bool _isRunning;

        public event EventHandler? CanExecuteChanged;
        public event Action<Exception>? Failed;
        public event Action? Finished;
        public bool CanExecute(object? parameter) => Design.IsDesignMode || !_isRunning && (canExecute?.Invoke() ?? true);

        public async void Execute(object? parameter)
        {
            if (Design.IsDesignMode || !CanExecute(parameter))
            {
                return;
            }

            _isRunning = true;
            RaiseCanExecuteChanged();
            try
            {
                await execute();
            }
            catch (Exception exception)
            {
                Failed?.Invoke(exception);
            }
            finally
            {
                _isRunning = false;
                RaiseCanExecuteChanged();
                Finished?.Invoke();
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
