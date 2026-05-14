using System.Text;
using Whisper.net;

namespace pizzad;

public sealed class WhisperTranscriber : ITranscriber
{
    private readonly string _modelPath;
    private readonly int _threads;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _processorLock = new(1, 1);
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private bool _initialized;
    private bool _disposed;

    public WhisperTranscriber(string modelPath, int threads, ILogger logger)
    {
        _modelPath = modelPath;
        _threads = threads;
        _logger = logger;
    }

    public Task<bool> Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
            return Task.FromResult(true);
        if (string.IsNullOrWhiteSpace(_modelPath) || !File.Exists(_modelPath))
            throw new FileNotFoundException($"Whisper model file was not found: {_modelPath}", _modelPath);

        _factory = WhisperFactory.FromPath(_modelPath);
        var builder = _factory.CreateBuilder();
        if (_threads > 0)
            builder = builder.WithThreads(_threads);

        _processor = builder
            .WithLanguage("en")
            .WithNoContext()
            .WithSingleSegment()
            .Build();
        _initialized = true;
        _logger.LogInformation("Whisper initialized. ModelPath={ModelPath}; threads={Threads}", _modelPath, _threads);
        return Task.FromResult(true);
    }

    public async Task<string> TranscribeCall(MemoryStream wavData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _processor == null)
            throw new InvalidOperationException("Whisper model is not initialized.");

        await _processorLock.WaitAsync();
        try
        {
            wavData.Position = 0;
            var sb = new StringBuilder();
            await foreach (var result in _processor.ProcessAsync(wavData))
                sb.Append(result.Text).Append(' ');
            return sb.ToString();
        }
        finally
        {
            _processorLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _initialized = false;
        _processor?.Dispose();
        _factory?.Dispose();
        _processorLock.Dispose();
    }
}
