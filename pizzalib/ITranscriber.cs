namespace pizzalib
{
    public interface ITranscriber : IDisposable
    {
        Task<bool> Initialize();
        Task<string> TranscribeCall(MemoryStream wavData);
    }
}

