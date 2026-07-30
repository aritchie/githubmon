using Shiny;

namespace GitHubShine.Sync;

/// <summary>Who is holding the gate — only for what the UI says while it's held.</summary>
public enum SyncRunKind { Manual, Automatic }

/// <summary>
/// One sync run at a time, process-wide. <see cref="RepoSyncEngine"/> already stops two git
/// processes sharing a cache directory, but that isn't the whole problem: a background run and the
/// user's own "Run all" would interleave their results, and the page's busy state knows nothing
/// about the background one. Whoever gets here first runs; the other is told to come back later.
/// </summary>
[Singleton(AsSelf = true)]
public sealed class SyncGate
{
    readonly object sync = new();
    SyncRunKind? holder;

    /// <summary>Raised (off whatever thread released or acquired) when a run starts or ends.</summary>
    public event EventHandler? BusyChanged;

    public bool IsBusy
    {
        get { lock (this.sync) return this.holder is not null; }
    }

    /// <summary>What kind of run is in flight, or null when idle.</summary>
    public SyncRunKind? Holder
    {
        get { lock (this.sync) return this.holder; }
    }

    /// <summary>
    /// Takes the gate, or returns null when a run is already going. Dispose the lease to release
    /// it — never block waiting, since a run can take minutes.
    /// </summary>
    public IDisposable? TryAcquire(SyncRunKind kind)
    {
        lock (this.sync)
        {
            if (this.holder is not null)
                return null;
            this.holder = kind;
        }
        this.BusyChanged?.Invoke(this, EventArgs.Empty);
        return new Lease(this);
    }

    void Release()
    {
        lock (this.sync)
        {
            if (this.holder is null)
                return;
            this.holder = null;
        }
        this.BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    sealed class Lease(SyncGate gate) : IDisposable
    {
        int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.released, 1) == 0)
                gate.Release();
        }
    }
}
