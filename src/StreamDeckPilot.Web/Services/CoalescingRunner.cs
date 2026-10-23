namespace StreamDeckPilot.Web.Services;

/// <summary>
/// Runs a refresh at most once at a time and folds every request made meanwhile into one more run,
/// so a burst of pushed changes (a chatty topic) costs at most two refreshes, not one per change.
/// Driven only by requests: no timer, no polling.
/// </summary>
public sealed class CoalescingRunner(Func<Task> work, Action<Exception>? onError = null)
{
    private const int Idle = 0, Running = 1, Dirty = 2;
    private int _state;

    /// <summary>Asks for a run. Returns at once; the run happens on the caller's thread pool.</summary>
    public void Request()
    {
        while (true)
        {
            switch (Volatile.Read(ref _state))
            {
                case Idle:
                    if (Interlocked.CompareExchange(ref _state, Running, Idle) != Idle) continue;
                    _ = RunAsync();
                    return;
                case Running:
                    if (Interlocked.CompareExchange(ref _state, Dirty, Running) != Running) continue;
                    return;
                default:
                    return;   // already Dirty: the pending run covers this request
            }
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            try { await work(); }
            catch (Exception ex) { onError?.Invoke(ex); }

            if (Interlocked.CompareExchange(ref _state, Idle, Running) == Running) return;
            Volatile.Write(ref _state, Running);   // was Dirty: requests came in while running
        }
    }
}
