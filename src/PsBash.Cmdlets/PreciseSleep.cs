using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PsBash.Cmdlets;

/// <summary>
/// A sleep that ends within about a millisecond of its duration, as on Linux. On Windows <see cref="Thread.Sleep(int)"/>
/// wakes on the system tick (15.6 ms unless some process raised the resolution): measured, 10 ms slept 13-35 ms,
/// 20 ms 21-38 and 30 ms 35-49, so <c>{ sleep 0.03; echo 3; } &amp; { sleep 0.02; echo 2; } &amp;</c> finished in a tie
/// decided by start order. A high-resolution waitable timer (Windows 10 1803+) wakes on time without changing the
/// system's timer resolution (10.4-12.9, 20.2-21.0, 30.2-38.0 ms measured on the same loaded host). Elsewhere, and
/// where that timer cannot be made, <see cref="Thread.Sleep(int)"/>.
/// </summary>
internal static class PreciseSleep
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;

    /// <summary>Sleep <paramref name="ms"/> milliseconds (never less).</summary>
    public static void For(int ms)
    {
        if (ms <= 0) return;
        if (OperatingSystem.IsWindows() && TryHighResolution(ms)) return;
        Thread.Sleep(ms);
    }

    private static bool TryHighResolution(int ms)
    {
        SafeWaitHandle timer;
        try { timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess); }
        catch (Exception) { return false; } // EntryPointNotFound / DllNotFound: an older Windows
        using (timer)
        {
            if (timer.IsInvalid) return false;
            var sw = Stopwatch.StartNew();
            long target = ms * TimeSpan.TicksPerMillisecond;
            while (true)
            {
                long left = target - sw.Elapsed.Ticks;
                if (left <= 0) return true;
                long due = -left; // negative: relative, in 100 ns units (= TimeSpan ticks)
                if (!SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    left = target - sw.Elapsed.Ticks;
                    if (left > 0) Thread.Sleep(TimeSpan.FromTicks(left));
                    return true;
                }
                WaitForSingleObject(timer, Infinite);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr arg,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
