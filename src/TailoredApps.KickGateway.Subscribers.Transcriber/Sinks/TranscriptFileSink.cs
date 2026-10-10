using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subscribers.Transcriber.Sinks;

/// <summary>
/// Appends transcripts to <c>{OutputDir}/{slug}/{yyyy-MM-dd}.txt</c> (one human-readable line per
/// slice: UTC times, language, text) and <c>…/{yyyy-MM-dd}.jsonl</c> (the full <see cref="LiveTranscript"/> per
/// line). The day is the UTC date of the slice's start. Writes are serialized and flushed per
/// transcript so files are always complete up to the last line.
/// </summary>
public sealed class TranscriptFileSink : ITranscriptSink, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // No BOM: Encoding.UTF8 would stamp one on the first line, which breaks line-oriented .jsonl readers.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _dir;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TranscriptFileSink(string outputDir) => _dir = outputDir;

    public string Name => "files";

    public string OutputDir => _dir;

    public async Task WriteAsync(LiveTranscript t, CancellationToken ct)
    {
        var slug = Sanitize(string.IsNullOrWhiteSpace(t.BroadcasterSlug) ? "unknown" : t.BroadcasterSlug);
        var day = t.StartedAt.ToString("yyyy-MM-dd");
        var channelDir = Path.Combine(_dir, slug);

        var lang = string.IsNullOrEmpty(t.Language) ? "" : $" [{t.Language}]";
        var txtLine = $"[{t.StartedAt:HH:mm:ss}-{t.EndedAt:HH:mm:ss}]{lang} {t.Text}{Environment.NewLine}";
        var jsonLine = JsonSerializer.Serialize(t, Json) + Environment.NewLine;

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(channelDir);
            await File.AppendAllTextAsync(Path.Combine(channelDir, day + ".txt"), txtLine, Utf8NoBom, ct);
            await File.AppendAllTextAsync(Path.Combine(channelDir, day + ".jsonl"), jsonLine, Utf8NoBom, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Sanitize(string slug) =>
        new(slug.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

    public void Dispose() => _gate.Dispose();
}
