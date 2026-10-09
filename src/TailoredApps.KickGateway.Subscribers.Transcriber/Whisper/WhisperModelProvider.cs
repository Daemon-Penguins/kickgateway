using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace TailoredApps.KickGateway.Subscribers.Transcriber.Whisper;

/// <summary>
/// Resolves the configured GGML model to a file (downloading it from Hugging Face on first use
/// into <see cref="TranscriberOptions.ModelDir"/>) and builds the shared <see cref="WhisperFactory"/>,
/// picking the GPU runtime (Vulkan/CUDA) when asked and available, CPU otherwise.
/// </summary>
public sealed class WhisperModelProvider
{
    private readonly TranscriberOptions _opts;
    private readonly ILogger<WhisperModelProvider> _log;

    private static int _nativeLogHooked;
    private static int _coopmatConfigured;

    public WhisperModelProvider(TranscriberOptions opts, ILogger<WhisperModelProvider> log)
    {
        _opts = opts;
        _log = log;
        ModelType = ParseEnum<GgmlType>(opts.Model, nameof(opts.Model));
        Quantization = ParseEnum<QuantizationType>(opts.Quantization, nameof(opts.Quantization));
        HookNativeLog();
    }

    /// <summary>
    /// Routes whisper.cpp / ggml's own log lines into ILogger. Backend device enumeration
    /// (<c>ggml_vulkan: 0 = Intel(R) Arc(TM) …</c>) and allocation / device-lost errors only show up
    /// here — without this a GPU fault is just an opaque <see cref="System.Runtime.InteropServices.SEHException"/>.
    /// </summary>
    private void HookNativeLog()
    {
        if (Interlocked.Exchange(ref _nativeLogHooked, 1) != 0) return;
        var log = _log;
        LogProvider.AddLogger((level, message) =>
        {
            var text = (message ?? "").TrimEnd('\r', '\n', ' ');
            if (text.Length == 0) return;
            // Backend/device enumeration is worth seeing once at Information; the per-call
            // "whisper_backend_init_gpu" chatter (a new whisper state per chunk) stays at Debug.
            var isBackendInfo = text.Contains("ggml_vulkan", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("ggml_cuda", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("ggml_metal", StringComparison.OrdinalIgnoreCase);
            var mapped = level switch
            {
                WhisperLogLevel.Error => LogLevel.Error,
                WhisperLogLevel.Warning => LogLevel.Warning,
                WhisperLogLevel.Info => isBackendInfo ? LogLevel.Information : LogLevel.Debug,
                _ => LogLevel.Trace,
            };
            log.Log(mapped, "whisper.cpp: {Message}", text);
        });
    }

    public GgmlType ModelType { get; }
    public QuantizationType Quantization { get; }

    /// <summary>Human-readable model id stamped on every transcript, e.g. <c>LargeV3Turbo/Q5_0</c>.</summary>
    public string ModelName => $"{ModelType}/{Quantization}";

    public string ModelDirectory => string.IsNullOrWhiteSpace(_opts.ModelDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kickgateway", "whisper")
        : Path.GetFullPath(_opts.ModelDir);

    public string ModelPath => Path.Combine(ModelDirectory, $"ggml-{ModelType}-{Quantization}.bin");

    /// <summary>Makes sure the model file exists locally, downloading it if needed. Returns its path.</summary>
    public async Task<string> EnsureModelAsync(CancellationToken ct)
    {
        var path = ModelPath;
        if (File.Exists(path))
        {
            _log.LogInformation("Whisper model {Model} found at {Path} ({Mb} MB)", ModelName, path, new FileInfo(path).Length / (1024 * 1024));
            return path;
        }

        Directory.CreateDirectory(ModelDirectory);
        var tmp = path + ".tmp";
        _log.LogInformation("Whisper model {Model} not cached — downloading to {Path} (one-off, can take a few minutes)", ModelName, path);

        try
        {
            await using var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ModelType, Quantization, ct);
            await using (var target = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long total = 0, nextLog = 100L << 20;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    if (total >= nextLog)
                    {
                        _log.LogInformation("Whisper model download: {Mb} MB so far", total / (1024 * 1024));
                        nextLog += 100L << 20;
                    }
                }
            }
            File.Move(tmp, path, overwrite: true);
            _log.LogInformation("Whisper model {Model} downloaded ({Mb} MB)", ModelName, new FileInfo(path).Length / (1024 * 1024));
            return path;
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Loads the model into a factory that owns the weights. <paramref name="useGpu"/> defaults to the
    /// configured value; the worker passes <c>false</c> to fall back to the CPU backend after repeated
    /// GPU failures (the native library order is fixed once loaded, but a Vulkan/CUDA build still runs
    /// on the CPU when the context is created with <c>use_gpu = false</c>).
    /// </summary>
    public WhisperFactory CreateFactory(string modelPath, bool? useGpu = null)
    {
        var gpu = useGpu ?? _opts.UseGpu;

        if (_opts.DisableVulkanCoopmat && Interlocked.Exchange(ref _coopmatConfigured, 1) == 0)
        {
            // Must land in the C runtime's environment before ggml-vulkan initialises (first native call).
            var ok = NativeEnvironment.Set("GGML_VK_DISABLE_COOPMAT", "1");
            _log.LogInformation("Vulkan cooperative-matrix shaders disabled (GGML_VK_DISABLE_COOPMAT=1, CRT update {Result}) — check the ggml_vulkan device line for 'matrix cores: none'",
                ok ? "ok" : "FAILED, set the variable in the process environment instead");
        }

        RuntimeOptions.RuntimeLibraryOrder = _opts.UseGpu
            ? [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
            : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];

        var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = gpu, GpuDevice = _opts.GpuDevice });
        _log.LogInformation("Whisper runtime: {Library} (GPU: {Gpu}, device {Device}, threads {Threads}) — {Info}",
            RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown", gpu, _opts.GpuDevice, _opts.EffectiveThreads, WhisperFactory.GetRuntimeInfo());
        return factory;
    }

    /// <summary>Accepts enum names loosely: <c>LargeV3Turbo</c>, <c>large-v3-turbo</c>, <c>q5_0</c>, <c>Q5-0</c> all work.</summary>
    public static T ParseEnum<T>(string value, string optionName) where T : struct, Enum
    {
        var wanted = Strip(value);
        foreach (var name in Enum.GetNames<T>())
            if (string.Equals(Strip(name), wanted, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<T>(name);

        throw new ArgumentException(
            $"Transcriber:{optionName} '{value}' is not a known {typeof(T).Name}. Valid values: {string.Join(", ", Enum.GetNames<T>())}.");

        static string Strip(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
    }
}
