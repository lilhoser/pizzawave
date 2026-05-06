using System.Collections.Concurrent;
using System.Text.Json;

namespace pizzad;

public sealed class EventStream
{
    private readonly ConcurrentDictionary<Guid, ChannelClient> _clients = new();
    private long _nextId;

    public async Task PublishAsync(string type, object payload, CancellationToken ct = default)
    {
        var ev = new SseEvent(type, payload, Interlocked.Increment(ref _nextId));
        foreach (var client in _clients.Values)
            await client.Writer.WriteAsync(ev, ct);
    }

    public async Task StreamAsync(HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.ContentType = "text/event-stream";

        var id = Guid.NewGuid();
        var client = ChannelClient.Create();
        _clients[id] = client;

        try
        {
            await WriteEventAsync(context, new SseEvent("connected", new { serverTimeUtc = DateTime.UtcNow }, Interlocked.Increment(ref _nextId)), ct);
            await foreach (var ev in client.Reader.ReadAllAsync(ct))
                await WriteEventAsync(context, ev, ct);
        }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    private static async Task WriteEventAsync(HttpContext context, SseEvent ev, CancellationToken ct)
    {
        await context.Response.WriteAsync($"id: {ev.Id}\n", ct);
        await context.Response.WriteAsync($"event: {ev.Type}\n", ct);
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(ev.Payload, EngineConfig.JsonOptions())}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private sealed record ChannelClient(
        System.Threading.Channels.ChannelWriter<SseEvent> Writer,
        System.Threading.Channels.ChannelReader<SseEvent> Reader)
    {
        public static ChannelClient Create()
        {
            var channel = System.Threading.Channels.Channel.CreateBounded<SseEvent>(new System.Threading.Channels.BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
            });
            return new ChannelClient(channel.Writer, channel.Reader);
        }
    }
}
