using System.Diagnostics;

namespace TheCloser.Shared;

// The elevated AutoHotkey logon task passes its priority class to the script and to every process
// the script launches. Task Scheduler's default is Below Normal, and a below-normal thread starves
// whenever normal-priority work saturates the cores: in the 2026-09-02 investigation one SendInput
// inside the daemon took 10 s under such load, and 1.5 ms at Normal under the same load. The task
// is now registered at Normal; this raise is the backstop for a manual launch or a stale task.
public static class ProcessPriority
{
    // True when the class was raised; false when it was already Normal or deliberately higher.
    // Throws what Process.PriorityClass throws; callers on a startup path use the logging overload.
    public static bool EnsureAtLeastNormal(Process process)
    {
        if (process.PriorityClass is not (ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle))
        {
            return false;
        }

        process.PriorityClass = ProcessPriorityClass.Normal;

        return true;
    }

    // Startup form: never throws, because the raise is an optimization and a failure to read or set
    // the class (a job object restricting priority, for example) must not stop the daemon or the
    // fallback app from running at all. Logs the raise, or the reason it could not be attempted.
    public static void EnsureCurrentAtLeastNormal(Action<string> log, Func<Process>? currentProcess = null)
    {
        try
        {
            using var process = (currentProcess ?? Process.GetCurrentProcess)();

            if (EnsureAtLeastNormal(process))
            {
                log("Raised the process priority class to Normal (inherited a lower class from the launcher).");
            }
        }
        catch (Exception ex)
        {
            // Win32Exception from GetPriorityClass/SetPriorityClass and InvalidOperationException from a
            // stale handle are the known shapes; anything else is equally not worth failing startup for.
            log($"Could not adjust the process priority class ({ex.GetType().Name}: {ex.Message}); continuing at the inherited class.");
        }
    }
}
