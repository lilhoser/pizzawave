using System.Security.Cryptography;
using System.Text;
using pizzalib;

namespace pizzapi;

internal static class CallHash
{
    public static string Compute(TranscribedCall call)
    {
        var raw = $"{call.StartTime}|{call.Talkgroup}|{call.Transcription}";
        using var sha = SHA1.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }

    public static string ComputeCallId(TranscribedCall call)
    {
        var hash = Compute(call);
        return "C" + hash.Substring(0, 12);
    }
}
