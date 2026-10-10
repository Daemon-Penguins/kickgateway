using System.Text;
using System.Text.Json;

namespace TailoredApps.KickGateway.Subtitles;

/// <summary>Server-Sent Events plumbing: wire format + the streaming loop with keep-alives.</summary>
public static class Sse
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One SSE frame: <c>id</c> (the transcript's id, also for a translation update), <c>event</c>, single-line JSON <c>data</c>, blank line.</summary>
    public static string Format(FeedMessage message) =>
        $"id: {message.Event.Id}\nevent: {message.Kind}\ndata: {JsonSerializer.Serialize(message.Event, Json)}\n\n";

    public static string Format(SubtitleEvent ev) => Format(new FeedMessage(FeedMessage.Transcript, ev));

    /// <summary>Reads the resume point: the <c>Last-Event-ID</c> header EventSource sends on reconnect, else <c>?after=</c>.</summary>
    public static long ResumeAfter(HttpRequest request)
    {
        if (long.TryParse(request.Headers["Last-Event-ID"].FirstOrDefault(), out var header) && header > 0) return header;
        if (long.TryParse(request.Query["after"].FirstOrDefault(), out var query) && query > 0) return query;
        return 0;
    }

    /// <summary>
    /// Streams <paramref name="slug"/>'s transcripts to one browser until it disconnects. A keep-alive
    /// comment goes out every <paramref name="keepAlive"/> so proxies don't close an idle stream while
    /// the streamer is quiet; writes are serialized because the keep-alive timer and the feed race.
    /// </summary>
    public static async Task StreamAsync(HttpContext ctx, TranscriptFeed feed, string slug, long afterId, TimeSpan keepAlive)
    {
        var response = ctx.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.Headers.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no"; // nginx-style proxies: don't buffer the stream
        await response.StartAsync(ctx.RequestAborted);

        var gate = new SemaphoreSlim(1, 1);
        async Task WriteAsync(string frame, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                await response.WriteAsync(frame, Encoding.UTF8, ct);
                await response.Body.FlushAsync(ct);
            }
            finally
            {
                gate.Release();
            }
        }

        var ct = ctx.RequestAborted;
        await WriteAsync($"retry: 3000\n: connected {slug}\n\n", ct);

        using var timer = new PeriodicTimer(keepAlive);
        var keepAliveLoop = Task.Run(async () =>
        {
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                    await WriteAsync(": keep-alive\n\n", ct);
            }
            catch (OperationCanceledException) { }
        }, ct);

        try
        {
            await foreach (var message in feed.SubscribeAsync(slug, afterId, ct))
                await WriteAsync(Format(message), ct);
        }
        catch (OperationCanceledException)
        {
            // viewer left
        }
        finally
        {
            timer.Dispose();
            try { await keepAliveLoop; } catch { /* cancelled */ }
        }
    }
}
