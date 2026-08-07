using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;

namespace GitSvnShuttle.Vsix;

internal sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool>? canExecute;
    private bool isRunning;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object parameter) => !isRunning && (canExecute?.Invoke() ?? true);

    public void Execute(object parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

#pragma warning disable VSSDK007 // ICommand is a synchronous event boundary; FileAndForget observes failures.
        ThreadHelper.JoinableTaskFactory.RunAsync(ExecuteAsync)
            .FileAndForget("GitSvnShuttle/Command");
#pragma warning restore VSSDK007
    }

    private async Task ExecuteAsync()
    {
        isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        finally
        {
            isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
