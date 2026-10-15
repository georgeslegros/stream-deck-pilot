using System.Collections.Concurrent;

namespace StreamDeckPilot.Api.Endpoints;

/// <summary>
/// Serialises the save-and-apply sequence per device (#79). A config PUT saves the file, rebuilds
/// the topic index and desired state, and refreshes retained values; two overlapping PUTs for the
/// same serial could otherwise interleave those steps and leave the live projection built from a
/// different save than the one on disk. Different devices don't wait on each other.
/// </summary>
public sealed class DeviceConfigGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public async Task<IDisposable> EnterAsync(string serial, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(serial, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
