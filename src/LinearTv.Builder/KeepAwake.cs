using System.Runtime.InteropServices;

namespace LinearTv.Builder;

/// <summary>Stops the machine sleeping while a build runs. Windows only; elsewhere, nothing.</summary>
internal static class KeepAwake
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    /// <summary>
    /// Asks Windows not to sleep for as long as the calling thread runs: the main thread, which
    /// waits for the build to finish. Windows drops the request when the process exits.
    /// </summary>
    public static void WhileRunning()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = SetThreadExecutionState(EsContinuous | EsSystemRequired);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
