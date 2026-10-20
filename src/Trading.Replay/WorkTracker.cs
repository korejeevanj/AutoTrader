namespace Trading.Replay;

/// <summary>Counts queued, acquiring, playing and publishing work so a caller can await quiescence.</summary>
internal sealed class WorkTracker
{
    private readonly object gate = new();
    private int active;
    private TaskCompletionSource idle = Completed();
    private static TaskCompletionSource Completed() { var done = new TaskCompletionSource(); done.SetResult(); return done; }
    public void Begin() { lock (gate) { if (active++ == 0) idle = new(TaskCreationOptions.RunContinuationsAsynchronously); } }
    public void End() { lock (gate) { if (--active == 0) idle.TrySetResult(); } }
    public Task WhenIdle() { lock (gate) return idle.Task; }
}
