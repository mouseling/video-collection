using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

// Vcm.Core and Vcm.Media shadow LibVLCSharp's Core and Media types from inside this namespace.
using VlcCore = LibVLCSharp.Shared.Core;

namespace Vcm.Media;

/// <summary>
/// Finds libvlc and brings up a single shared <see cref="LibVLC"/> instance.
///
/// On Windows the VideoLAN.LibVLC.Windows package drops the native DLLs next to the executable
/// and LibVLCSharp finds them unaided. On Linux there is no such package, so development uses a
/// VLC runtime unpacked into <c>.devtools/</c>; the plugin directory has to be passed explicitly
/// because .NET does not propagate <c>Environment.SetEnvironmentVariable</c> to native getenv.
/// </summary>
public static class VlcRuntime
{
    private static readonly object Gate = new();
    private static LibVLC? _instance;
    private static string? _failure;

    /// <summary>Non-null when initialisation failed, for the UI to surface instead of crashing.</summary>
    public static string? Failure => _failure;

    public static LibVLC? Instance
    {
        get
        {
            lock (Gate)
            {
                if (_instance is not null || _failure is not null) return _instance;
                try { _instance = Create(); }
                catch (Exception ex) { _failure = ex.Message; }
                return _instance;
            }
        }
    }

    private static readonly bool Debug =
        Environment.GetEnvironmentVariable("VCM_VLC_DEBUG") == "1";

    private static void Trace(string msg)
    {
        if (Debug) Console.Error.WriteLine($"[vlc] {msg}");
    }

    private static LibVLC Create()
    {
        var options = new List<string>
        {
            "--no-video-title-show",   // no filename burned over the first seconds of video
        };
        options.Add(Debug ? "-vv" : "--quiet");

        if (OperatingSystem.IsLinux())
        {
            var dir = FindLinuxRuntime()
                ?? throw new DirectoryNotFoundException(
                    "No libvlc found. Expected .devtools/vlc-lib next to the repository root.");

            // Core.Initialize(path) throws on Linux — it insists libvlc live in /usr/lib. Instead
            // load the libraries by absolute path and teach the runtime to resolve LibVLCSharp's
            // P/Invokes to them, which needs neither a system install nor LD_LIBRARY_PATH.
            //
            // These must be RTLD_GLOBAL. VLC's plugins are dlopened later and resolve their
            // symbols back into libvlccore; NativeLibrary.Load would load it RTLD_LOCAL, leaving
            // those symbols invisible and crashing the process as soon as a plugin loads.
            Trace($"runtime dir: {dir}");
            var core = DlOpenGlobal(Path.Combine(dir, "libvlccore.so.9"));
            Trace("libvlccore loaded");
            var vlc = DlOpenGlobal(Path.Combine(dir, "libvlc.so.5"));
            Trace("libvlc loaded");

            // VLC's own plugins link against these private helpers, which sit one level down.
            foreach (var helper in new[] { "libvlc_xcb_events.so.0", "libvlc_pulse.so.0" })
            {
                var p = Path.Combine(dir, "vlc", helper);
                if (File.Exists(p)) DlOpenGlobal(p);
            }

            NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, (name, _, _) => name switch
            {
                "libvlc" => vlc,
                "libvlccore" => core,
                _ => IntPtr.Zero,
            });

            // VLC 3 dropped --plugin-path and reads VLC_PLUGIN_PATH instead. .NET's
            // Environment.SetEnvironmentVariable only updates its own managed copy on Unix, so
            // the native getenv would never see it — set it through libc directly.
            SetEnv("VLC_PLUGIN_PATH", Path.Combine(dir, "vlc", "plugins"), 1);
            Trace("plugin path set");
        }
        else
        {
            VlcCore.Initialize();
        }

        Trace($"calling libvlc_new: {string.Join(" ", options)}");
        var instance = new LibVLC(options.ToArray());
        Trace("libvlc_new ok");
        return instance;
    }

    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    private static extern int SetEnv(string name, string value, int overwrite);

    [DllImport("libc", EntryPoint = "dlopen")]
    private static extern IntPtr DlOpen(string file, int flags);

    [DllImport("libc", EntryPoint = "dlerror")]
    private static extern IntPtr DlError();

    private const int RtldLazy = 0x0001;
    private const int RtldGlobal = 0x0100;

    private static IntPtr DlOpenGlobal(string path)
    {
        var handle = DlOpen(path, RtldLazy | RtldGlobal);
        if (handle == IntPtr.Zero)
        {
            var err = Marshal.PtrToStringAnsi(DlError()) ?? "unknown error";
            throw new DllNotFoundException($"Could not load {path}: {err}");
        }
        return handle;
    }

    /// <summary>Walks up from the binary looking for the unpacked development runtime.</summary>
    private static string? FindLinuxRuntime()
    {
        var fromEnv = Environment.GetEnvironmentVariable("VCM_VLC_LIB");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(Path.Combine(fromEnv, "libvlc.so.5")))
            return fromEnv;

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, ".devtools", "vlc-lib");
            if (File.Exists(Path.Combine(candidate, "libvlc.so.5"))) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }
}
