using System.Diagnostics;
using System.Text.Json;

namespace pizzad;

public sealed class FasterWhisperTranscriber : ITranscriber
{
    private readonly EngineConfig _config;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private Task? _stderrTask;
    private bool _disposed;

    public FasterWhisperTranscriber(EngineConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task<bool> Initialize()
    {
        EnsureProcess();
        return Task.FromResult(true);
    }

    public async Task<string> TranscribeCall(MemoryStream wavData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync();
        var temp = Path.Combine(Path.GetTempPath(), $"pizzawave-fw-{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(temp, wavData.ToArray());
            var process = EnsureProcess();
            var request = JsonSerializer.Serialize(new { id = 0, audio_path = temp });
            await process.StandardInput.WriteLineAsync(request);
            await process.StandardInput.FlushAsync();
            var line = await process.StandardOutput.ReadLineAsync();
            if (line == null)
            {
                Restart();
                throw new InvalidOperationException("faster-whisper worker exited without returning a transcription response.");
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error) && !string.IsNullOrWhiteSpace(error.GetString()))
                throw new InvalidOperationException("faster-whisper failed: " + error.GetString());
            return root.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty;
        }
        finally
        {
            TryDelete(temp);
            _gate.Release();
        }
    }

    private Process EnsureProcess()
    {
        if (_process is { HasExited: false })
            return _process;

        var python = _config.Transcription.FasterWhisperPythonPath;
        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python))
            throw new FileNotFoundException($"faster-whisper Python runtime was not found: {python}", python);

        var script = ResolveScriptPath();
        if (!File.Exists(script))
            throw new FileNotFoundException($"faster-whisper worker script was not found: {script}", script);

        var psi = new ProcessStartInfo
        {
            FileName = python,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_config.Transcription.FasterWhisperModel);
        psi.ArgumentList.Add("--device");
        psi.ArgumentList.Add(_config.Transcription.FasterWhisperDevice);
        psi.ArgumentList.Add("--compute-type");
        psi.ArgumentList.Add(_config.Transcription.FasterWhisperComputeType);
        psi.ArgumentList.Add("--cpu-threads");
        psi.ArgumentList.Add(Math.Max(1, _config.Transcription.WhisperThreads).ToString());
        psi.ArgumentList.Add("--workers");
        psi.ArgumentList.Add(Math.Max(1, _config.Transcription.FasterWhisperWorkers).ToString());
        psi.ArgumentList.Add("--vad-filter");
        psi.ArgumentList.Add(_config.Transcription.FasterWhisperVadFilter ? "true" : "false");

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start faster-whisper worker.");
        _stderrTask = Task.Run(async () =>
        {
            try
            {
                while (_process is { HasExited: false } && await _process.StandardError.ReadLineAsync() is { } line)
                    _logger.LogInformation("faster-whisper: {Line}", line);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "faster-whisper stderr reader stopped.");
            }
        });
        _logger.LogInformation(
            "Started faster-whisper worker model={Model} compute={ComputeType} device={Device} threads={Threads}",
            _config.Transcription.FasterWhisperModel,
            _config.Transcription.FasterWhisperComputeType,
            _config.Transcription.FasterWhisperDevice,
            Math.Max(1, _config.Transcription.WhisperThreads));
        return _process;
    }

    private string ResolveScriptPath()
    {
        if (!string.IsNullOrWhiteSpace(_config.Transcription.FasterWhisperScriptPath))
            return _config.Transcription.FasterWhisperScriptPath;
        var appScript = Path.Combine(AppContext.BaseDirectory, "scripts", "faster_whisper_worker.py");
        if (File.Exists(appScript))
            return appScript;
        return "/usr/lib/pizzawave/scripts/faster_whisper_worker.py";
    }

    private void Restart()
    {
        DisposeProcess();
        EnsureProcess();
    }

    private void DisposeProcess()
    {
        var process = _process;
        _process = null;
        if (process == null)
            return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("""{"command":"shutdown"}""");
                process.StandardInput.Flush();
                if (!process.WaitForExit(1500))
                    process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisposeProcess();
        _gate.Dispose();
    }
}
