using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TailoredApps.KickGateway.Subscribers.Transcriber;

/// <summary>
/// One long-lived ffmpeg process per channel: the raw HLS segments (MPEG-TS, or fMP4 with its init
/// written first) are streamed into stdin as one continuous container, and ffmpeg emits only the
/// audio track as 16 kHz mono float32 PCM on stdout — exactly what Whisper wants. Video is dropped
/// inside ffmpeg (<c>-vn</c>), so nothing heavy is ever decoded on our side.
/// </summary>
public sealed class AudioDecoder : IAsyncDisposable
{
    public const int SampleRate = 16_000;

    private readonly Process _proc;
    private readonly Stream _stdin;
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private readonly ILogger _log;
    private readonly string _slug;

    public AudioDecoder(string ffmpegPath, string slug, Action<float[]> onSamples, ILogger log)
    {
        _slug = slug;
        _log = log;
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-nostdin", "-loglevel", "error",
                     "-fflags", "+discardcorrupt",
                     "-probesize", "2M", "-analyzeduration", "3M",
                     "-i", "pipe:0",
                     "-vn", "-sn", "-dn",
                     "-ac", "1", "-ar", SampleRate.ToString(),
                     "-flush_packets", "1",
                     "-f", "f32le", "pipe:1",
                 })
            psi.ArgumentList.Add(a);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start ffmpeg ({ffmpegPath}).");
        _stdin = _proc.StandardInput.BaseStream;
        _stdoutPump = Task.Run(() => PumpPcmAsync(_proc.StandardOutput.BaseStream, onSamples));
        _stderrPump = Task.Run(PumpStderrAsync);
    }

    public bool HasExited => _proc.HasExited;

    /// <summary>Feeds one segment (or the fMP4 init) into ffmpeg.</summary>
    public async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        if (data.Length == 0 || _proc.HasExited) return;
        await _stdin.WriteAsync(data, ct);
        await _stdin.FlushAsync(ct);
    }

    /// <summary>Runs <c>ffmpeg -version</c> and returns its first line, or throws with the reason it could not be started.</summary>
    public static string Probe(string ffmpegPath)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-version");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{ffmpegPath}'.");
        var firstLine = proc.StandardOutput.ReadLine() ?? "";
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(10_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"'{ffmpegPath} -version' did not finish in 10 s.");
        }
        if (proc.ExitCode != 0) throw new InvalidOperationException($"'{ffmpegPath} -version' exited with code {proc.ExitCode}.");
        return firstLine;
    }

    private async Task PumpPcmAsync(Stream stdout, Action<float[]> onSamples)
    {
        var buf = new byte[64 * 1024];
        var carry = 0; // bytes of an incomplete float left over from the previous read
        try
        {
            while (true)
            {
                var n = await stdout.ReadAsync(buf.AsMemory(carry, buf.Length - carry));
                if (n == 0) break;
                var total = carry + n;
                var whole = total - total % sizeof(float);
                if (whole > 0)
                    onSamples(MemoryMarshal.Cast<byte, float>(buf.AsSpan(0, whole)).ToArray());
                carry = total - whole;
                if (carry > 0) Buffer.BlockCopy(buf, whole, buf, 0, carry);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Process is being torn down.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[{Slug}] PCM pump failed — decoder will be restarted on the next segment", _slug);
        }
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (await _proc.StandardError.ReadLineAsync() is { } line)
                if (!string.IsNullOrWhiteSpace(line))
                    _log.LogDebug("[{Slug}] ffmpeg: {Line}", _slug, line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Closes stdin so ffmpeg drains what it has, then waits briefly before killing it.</summary>
    public async ValueTask DisposeAsync()
    {
        try { _stdin.Close(); } catch { /* already gone */ }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { _proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
        await Task.WhenAll(_stdoutPump, _stderrPump).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        _proc.Dispose();
    }
}
