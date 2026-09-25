using System.Security.Principal;

namespace BtSwitcher.Windows;

/// <summary>Serializes modifying transactions across the extension and diagnostic CLI in this user's session.</summary>
public sealed class WindowsOperationLease : IDisposable
{
    private Semaphore? semaphore;
    private WindowsOperationLease(Semaphore semaphore) => this.semaphore = semaphore;

    public static WindowsOperationLease? TryAcquire()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var name = @"Local\BtSwitcher.Operation." + identity.User!.Value;
        var semaphore = new Semaphore(1, 1, name);
        if (semaphore.WaitOne(0)) return new(semaphore);
        semaphore.Dispose();
        return null;
    }

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref semaphore, null);
        if (owned is null) return;
        owned.Release();
        owned.Dispose();
    }
}
