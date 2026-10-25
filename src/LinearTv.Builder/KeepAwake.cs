using System.Runtime.InteropServices;

namespace LinearTv.Builder;

/// <summary>Stops the machine sleeping while a build runs. Windows only; elsewhere, nothing.</summary>
internal static class KeepAwake
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    /// <summary>
    /// Asks Windows not to sleep for as long as this process runs.
    /// </summary>
    /// <remarks>
    /// Windows ties the request to the thread that makes it and drops it when that thread ends.
    /// Made from async code it lands on a thread-pool thread, which .NET retires when idle: the
    /// PC then slept mid-build ("System Idle", 2026-10-25). So it's made from a thread of its own
    /// that lives until the process exits.
    /// </remarks>
    public static void WhileRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var thread = new Thread(() =>
        {
            _ = SetThreadExecutionState(EsContinuous | EsSystemRequired);
            Thread.Sleep(Timeout.Infinite);
        })
        {
            IsBackground = true,
            Name = "keep awake",
        };
        thread.Start();
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
