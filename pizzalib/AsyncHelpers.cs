using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static pizzalib.TraceLogger;

namespace pizzalib
{
    /// <summary>
    /// Helper utilities for managing fire‑and‑forget tasks and global cancellation.
    /// </summary>
    public static class AsyncHelpers
    {
        /// <summary>
        /// Global cancellation source that can be cancelled on application shutdown.
        /// </summary>
        public static readonly CancellationTokenSource GlobalCts = new CancellationTokenSource();

        // Thread‑safe collection of fire‑and‑forget tasks.
        private static readonly ConcurrentBag<Task> RunningTasks = new ConcurrentBag<Task>();

        /// <summary>
        /// Register a fire‑and‑forget task so it can be awaited/cancelled on shutdown.
        /// </summary>
        public static void Register(Task task)
        {
            if (task != null)
                RunningTasks.Add(task);
        }

        /// <summary>
        /// Cancel all registered tasks and wait for them to complete, up to the specified timeout.
        /// </summary>
        public static async Task ShutdownAsync(TimeSpan timeout)
        {
            try
            {
                GlobalCts.Cancel();
                var delay = Task.Delay(timeout);
                var whenAll = Task.WhenAll(RunningTasks.ToArray());
                await Task.WhenAny(whenAll, delay).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.CallManager, System.Diagnostics.TraceEventType.Error,
                    $"AsyncHelpers.ShutdownAsync error: {ex.Message}");
            }
        }
    }
}
