using System;
using System.Diagnostics;
using System.Text;
using Vintagestory.API.Common;

namespace Kaisentlaia.KsCartographyTableMod.API.Common
{
    // Timings are diagnostic only: disabled traces allocate nothing and do no logging.
    // A mark measures the stage since the previous mark, not cumulative elapsed time.
    internal sealed class CartographyPerformanceTrace : IDisposable
    {
        private readonly ICoreAPI api;
        private readonly string operation;
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly StringBuilder details = new();
        private double lastMarkMs;

        internal bool AlwaysLog { get; set; }

        private CartographyPerformanceTrace(ICoreAPI api, string operation)
        {
            this.api = api;
            this.operation = operation;
        }

        internal static CartographyPerformanceTrace Start(ICoreAPI api, string operation)
        {
            return api != null && Settings.VerboseDebug ? new CartographyPerformanceTrace(api, operation) : null;
        }

        internal void Detail(string detail)
        {
            details.Append(' ').Append(detail);
        }

        internal void Mark(string stage)
        {
            double elapsedMs = watch.Elapsed.TotalMilliseconds;
            Detail(FormattableString.Invariant($"{stage}Ms={elapsedMs - lastMarkMs:F1}"));
            lastMarkMs = elapsedMs;
        }

        public void Dispose()
        {
            watch.Stop();
            if (AlwaysLog || watch.Elapsed.TotalMilliseconds >= 100)
            {
                KsCartographyTableModSystem.DebugLog(api,
                    FormattableString.Invariant($"[perf] {operation} side={api.Side} totalMs={watch.Elapsed.TotalMilliseconds:F1}{details}"));
            }
        }
    }
}
