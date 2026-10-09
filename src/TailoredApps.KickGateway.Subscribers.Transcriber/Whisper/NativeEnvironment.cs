using System.Runtime.InteropServices;

namespace TailoredApps.KickGateway.Subscribers.Transcriber.Whisper;

/// <summary>
/// Sets environment variables so that <b>native</b> code sees them. ggml reads its tuning knobs
/// (<c>GGML_VK_DISABLE_COOPMAT</c>, …) with the C runtime's <c>getenv</c>. On Linux that is the
/// process environment, which <see cref="Environment.SetEnvironmentVariable(string, string)"/> updates.
/// On Windows the UCRT keeps its own copy captured at startup, so the Win32 API call .NET makes is
/// invisible to <c>getenv</c> — <c>_putenv_s</c> from <c>ucrtbase.dll</c> updates that copy (the
/// native runtime shares the system UCRT). Must run before the native library is first loaded.
/// </summary>
public static class NativeEnvironment
{
    [DllImport("ucrtbase.dll", EntryPoint = "_putenv_s", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PutEnvUcrt(string name, string value);

    [DllImport("msvcrt.dll", EntryPoint = "_putenv_s", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PutEnvMsvcrt(string name, string value);

    /// <summary>Sets the variable for managed code and for the C runtime(s). Returns false if the CRT update failed on Windows.</summary>
    public static bool Set(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        if (!OperatingSystem.IsWindows()) return true;

        var ok = false;
        try { ok = PutEnvUcrt(name, value) == 0; } catch (Exception) { /* no UCRT? */ }
        try { ok |= PutEnvMsvcrt(name, value) == 0; } catch (Exception) { /* legacy CRT absent — fine */ }
        return ok;
    }
}
