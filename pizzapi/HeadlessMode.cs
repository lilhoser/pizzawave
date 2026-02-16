using System.Diagnostics;
using System.Runtime.InteropServices;
using pizzalib;

namespace pizzapi
{
    using static TraceLogger;

    internal class HeadlessMode : StandaloneClient
    {
        public HeadlessMode() : base()
        {
            m_CallManager = new LiveCallManager(NewCallTranscribed);
        }

        protected override void PrintUsage(string Message = null)
        {
            if (!string.IsNullOrEmpty(Message))
            {
                Trace(TraceLoggerType.Headless, TraceEventType.Error, Message);
            }
            Trace(TraceLoggerType.Headless,
                  TraceEventType.Information,
                  "Usage: pizzapi --headless [--settings=<path>]");
        }

        protected override void NewCallTranscribed(TranscribedCall Call)
        {
            Trace(TraceLoggerType.Headless, TraceEventType.Information, $"{Call.ToString(m_Settings!)}");
        }

        public override async Task<int> Run(string[] Args)
        {
            var args = new List<string>();
            foreach (var arg in Args)
            {
                if (arg.ToLower().StartsWith("--headless") ||
                    arg.ToLower().StartsWith("-headless"))
                {
                    continue;
                }
                args.Add(arg);
            }

            TraceLogger.Initialize(true);
            pizzalib.TraceLogger.Initialize(true);
            Trace(TraceLoggerType.Headless, TraceEventType.Information, "");
            Trace(TraceLoggerType.Headless, TraceEventType.Information, "PizzaPi Headless Mode.");
            Trace(TraceLoggerType.Headless, TraceEventType.Information, "Starting callstream listener...");

            var result = await base.Run(args.ToArray());
            TraceLogger.Shutdown();
            pizzalib.TraceLogger.Shutdown();
            return result;
        }
    }
}
