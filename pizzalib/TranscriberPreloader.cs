using System.Diagnostics;
using static pizzalib.TraceLogger;

namespace pizzalib
{
    public static class TranscriberPreloader
    {
        public static async Task PreloadAsync(Settings settings, Action<string>? statusCallback = null)
        {
            if (settings == null)
            {
                return;
            }

            var engine = (settings.TranscriptionEngine ?? "whisper").Trim().ToLowerInvariant();
            ITranscriber? transcriber = null;

            try
            {
                statusCallback?.Invoke($"Loading {engine} model...");
                transcriber = engine == "vosk"
                    ? new VoskTranscriber(settings)
                    : new Whisper(settings);

                await transcriber.Initialize().ConfigureAwait(false);
                statusCallback?.Invoke($"{engine} model ready");
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.CallManager, TraceEventType.Error,
                    $"Model preload failed for {engine}: {ex.Message}");
                statusCallback?.Invoke($"{engine} model load failed: {ex.Message}");
            }
            finally
            {
                transcriber?.Dispose();
            }
        }
    }
}
