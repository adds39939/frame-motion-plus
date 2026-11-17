namespace FrameMotionPlus.Cuda;

/// <summary>
/// Base of every CUDA resource wrapper. It holds a reference to its <see cref="CudaContext"/> (and, for derived
/// resources such as a buffer mapped from external memory, to its parent), and frees the native resource when it and
/// every dependant have been disposed, with the context current. Disposal order therefore never matters.
/// Dispose must not race with use of the same object; distinct objects may be used from any threads.
/// <para>
/// Factories take the context reference before any driver call (so a disposed context fails before anything native
/// exists) and free whatever they allocated if a later step fails.
/// </para>
/// </summary>
public abstract class CudaObject : IDisposable
{
    private int _references = 1;
    private int _disposed;

    /// <summary>
    /// <paramref name="context"/> must already carry this object's reference (<see cref="CudaContext.AddReference"/>),
    /// taken by the factory before it allocated anything.
    /// </summary>
    private protected CudaObject(CudaContext context)
    {
        Context = context;
    }

    public CudaContext Context { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Releases this object's native resource once nothing depends on it any more. Freeing device or pinned memory
    /// (<c>cuMemFree</c>, <c>cuMemFreeHost</c>) blocks until all GPU work already queued in the context has finished,
    /// whether or not it uses the buffer (measured: a free waited out 300 ms of unrelated queued work), so never
    /// dispose buffers on the pacing or present thread.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Release();
        }
    }

    /// <summary>Frees the native resource. Runs once, with the context current.</summary>
    private protected abstract void ReleaseNative();

    /// <summary>A field check: throws <see cref="ObjectDisposedException"/> once <see cref="Dispose()"/> has been called.</summary>
    internal void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            ThrowDisposed();
        }
    }

    /// <summary>
    /// Keeps this object's native resource alive for a dependant until <see cref="Release"/>. Fails once the object is
    /// disposed or released, never reviving it.
    /// </summary>
    internal void AddReference()
    {
        while (true)
        {
            var references = Volatile.Read(ref _references);
            if (references == 0 || IsDisposed)
            {
                ThrowDisposed();
            }

            if (Interlocked.CompareExchange(ref _references, references + 1, references) == references)
            {
                return;
            }
        }
    }

    internal void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0)
        {
            return;
        }

        try
        {
            // First and unconditionally, so no signal can name the handle even if the native destroy cannot run.
            Unregister();

            // Never throws: a driver that is already shutting down simply leaves the resource to process exit.
            if (Context.TryEnsureCurrent())
            {
                ReleaseNative();
            }
        }
        finally
        {
            ReleaseParent();
            Context.Release();
        }
    }

    /// <summary>Removes the handle from <see cref="LiveHandles"/> (events, semaphores). Runs once, must not throw.</summary>
    private protected virtual void Unregister()
    {
    }

    /// <summary>Drops the reference a derived resource holds on the resource it came from.</summary>
    private protected virtual void ReleaseParent()
    {
    }

    private void ThrowDisposed() => throw new ObjectDisposedException(GetType().Name);
}
