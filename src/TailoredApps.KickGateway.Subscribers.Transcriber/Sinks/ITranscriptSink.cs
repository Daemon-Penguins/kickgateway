using TailoredApps.KickGateway.Contracts.Realtime.Media;

namespace TailoredApps.KickGateway.Subscribers.Transcriber.Sinks;

/// <summary>Where finished transcripts go. All registered sinks receive every transcript; a failing sink never blocks the others.</summary>
public interface ITranscriptSink
{
    string Name { get; }
    Task WriteAsync(LiveTranscript transcript, CancellationToken ct);
}
