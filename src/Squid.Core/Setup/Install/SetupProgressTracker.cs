using Squid.Message.Models.Setup;

namespace Squid.Core.Setup.Install;

/// <summary>Thread-safe progress of one install run, read by the wizard's progress polling.</summary>
public sealed class SetupProgressTracker
{
    private readonly object _gate = new();
    private SetupInstallState _state = SetupInstallState.Idle;
    private List<SetupStepProgress> _steps = new();
    private string _error;

    public bool IsRunning { get { lock (_gate) return _state == SetupInstallState.Running; } }

    public bool IsCompleted { get { lock (_gate) return _state == SetupInstallState.Completed; } }

    public bool TryBegin(IEnumerable<(string Key, string Title)> steps)
    {
        lock (_gate)
        {
            if (_state is SetupInstallState.Running or SetupInstallState.Completed) return false;

            _state = SetupInstallState.Running;
            _error = null;
            _steps = steps.Select(s => new SetupStepProgress { Key = s.Key, Title = s.Title, Status = SetupStepStatus.Pending }).ToList();

            return true;
        }
    }

    public void Mark(string key, SetupStepStatus status, string message = null)
    {
        lock (_gate) _steps = _steps.Select(s => s.Key == key ? s with { Status = status, Message = message } : s).ToList();
    }

    public void Complete()
    {
        lock (_gate) _state = SetupInstallState.Completed;
    }

    public void Fail(string error)
    {
        lock (_gate)
        {
            _state = SetupInstallState.Failed;
            _error = error;
        }
    }

    public SetupProgress Snapshot()
    {
        lock (_gate) return new SetupProgress { State = _state, Steps = _steps.ToList(), Error = _error };
    }
}
