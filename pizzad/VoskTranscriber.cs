using System.Text.Json;
using Vosk;

namespace pizzad;

public sealed class VoskTranscriber : ITranscriber
{
    private readonly string _modelPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _recognizerLock = new(1, 1);
    private Model? _model;
    private bool _initialized;
    private bool _disposed;

    public VoskTranscriber(string modelPath, ILogger logger)
    {
        _modelPath = modelPath;
        _logger = logger;
    }

    public async Task<bool> Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
            return true;
        if (!IsValidModelDirectory(_modelPath))
            throw new DirectoryNotFoundException($"Vosk model directory was not found or is invalid: {_modelPath}");

        await Task.Run(() =>
        {
            Vosk.Vosk.SetLogLevel(0);
            _model = new Model(_modelPath);
            _initialized = true;
            _logger.LogInformation("Vosk initialized. ModelPath={ModelPath}", _modelPath);
        }).ConfigureAwait(false);

        return true;
    }

    public async Task<string> TranscribeCall(MemoryStream wavData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _model == null)
            throw new InvalidOperationException("Vosk model is not initialized.");

        await _recognizerLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var recognizer = new VoskRecognizer(_model, 16000.0f);
            recognizer.SetWords(false);
            recognizer.SetPartialWords(false);
            recognizer.SetMaxAlternatives(0);

            var pcmData = ExtractPcmData(wavData);
            const int chunkSize = 4000;
            var offset = 0;
            while (offset < pcmData.Length)
            {
                var len = Math.Min(chunkSize, pcmData.Length - offset);
                recognizer.AcceptWaveform(pcmData.AsSpan(offset, len).ToArray(), len);
                offset += len;
            }

            var finalJson = recognizer.FinalResult();
            if (string.IsNullOrWhiteSpace(finalJson))
                return string.Empty;

            using var doc = JsonDocument.Parse(finalJson);
            return doc.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty;
        }
        finally
        {
            _recognizerLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _initialized = false;
        _model?.Dispose();
        _recognizerLock.Dispose();
    }

    private static bool IsValidModelDirectory(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Directory.Exists(path) &&
        Directory.Exists(Path.Combine(path, "am")) &&
        Directory.Exists(Path.Combine(path, "conf"));

    private static byte[] ExtractPcmData(MemoryStream wavData)
    {
        var data = wavData.ToArray();
        if (data.Length > 44 &&
            data[0] == (byte)'R' &&
            data[1] == (byte)'I' &&
            data[2] == (byte)'F' &&
            data[3] == (byte)'F' &&
            data[8] == (byte)'W' &&
            data[9] == (byte)'A' &&
            data[10] == (byte)'V' &&
            data[11] == (byte)'E')
        {
            var pcm = new byte[data.Length - 44];
            Buffer.BlockCopy(data, 44, pcm, 0, pcm.Length);
            return pcm;
        }

        return data;
    }
}
