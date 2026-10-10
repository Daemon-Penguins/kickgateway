namespace TailoredApps.KickGateway.Subtitles.Relay;

/// <summary>Logs what the relay served, per channel, every few minutes — the number to watch for transfer.</summary>
public sealed class RelayStatsLogger(VideoRelay relay, ILogger<RelayStatsLogger> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastServed = new Dictionary<string, long>(StringComparer.Ordinal);
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var s in relay.Snapshot())
                {
                    var delta = s.ServedBytes - lastServed.GetValueOrDefault(s.Slug);
                    lastServed[s.Slug] = s.ServedBytes;
                    if (delta == 0 && s.Viewers == 0) continue;
                    log.LogInformation("[relay:{Slug}] {Viewers} viewer(s), served {Mb:F1} MB in the last {Min} min ({TotalMb:F1} MB total), buffer {Sec:F0}s / {BufMb:F1} MB",
                        s.Slug, s.Viewers, delta / 1048576.0, Interval.TotalMinutes, s.ServedBytes / 1048576.0, s.BufferedSeconds, s.BufferedBytes / 1048576.0);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
