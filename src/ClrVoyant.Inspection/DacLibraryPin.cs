using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace ClrVoyant.Inspection;

/// <summary>
/// Keeps the target runtime's DAC (<c>libmscordaccore.so</c>) mapped into this process for the
/// rest of its life on Linux.
///
/// Why: on Linux the DAC carries its own copy of the runtime's PAL, which registers a
/// per-thread destructor (a <c>pthread_key_create</c> destructor living inside the DAC image) for
/// every thread that calls into it. ClrMD <c>dlclose</c>s the DAC when a <c>ClrRuntime</c> is
/// disposed (or fails to initialise), but those threads are still alive and still carry the key;
/// when any of them later exits, glibc calls the destructor at an address that is no longer mapped
/// (or now belongs to something else) and the whole process dies with SIGSEGV, long after the
/// inspection that caused it, usually while an unrelated thread-pool thread is retiring. Holding
/// one extra <c>dlopen</c> reference per DAC image makes ClrMD's <c>dlclose</c> a no-op, so the
/// destructor stays valid. Windows has no PAL and is not affected.
///
/// The images are loaded once per path and deliberately never freed: one DAC per runtime
/// version ever inspected (a few MB each), however many snapshots are taken.
/// </summary>
internal static class DacLibraryPin
{
    static readonly HashSet<string> Pinned = new(StringComparer.Ordinal);

    /// <summary>Create the runtime for <paramref name="clr"/>, first pinning its DAC so that
    /// nothing ClrMD does afterwards (dispose, or unloading after a failed initialisation) can
    /// unmap it. The DAC is the one ClrMD itself would load: the file next to the target's
    /// <c>libcoreclr.so</c>. When it is not there, ClrMD's own lookup and error apply unchanged.
    /// NOTE: in that case nothing is pinned, so a DAC that ClrMD finds elsewhere (a symbol server
    /// download) is still unloaded on dispose and the hazard above remains.</summary>
    public static ClrRuntime CreateRuntimePinned(ClrInfo clr)
    {
        var dac = OperatingSystem.IsLinux() ? DacPathFor(clr) : null;
        if (dac is null) return clr.CreateRuntime();

        lock (Pinned)
        {
            if (Pinned.Add(dac)) _ = NativeLibrary.Load(dac); // the handle is intentionally leaked: see the type docs
        }
        // ignoreMismatch: this is the DAC shipped beside the target's own libcoreclr, so it matches by
        // construction; ClrMD reports the target's version as 0.0 on Linux and would
        // otherwise reject a DAC it was handed explicitly as "mismatched".
        return clr.CreateRuntime(dac, ignoreMismatch: true);
    }

    // ClrMD lists the DAC it would load as a full path when it sits next to the target runtime.
    static string? DacPathFor(ClrInfo clr)
        => clr.DebuggingLibraries
            .Where(l => l.Kind == DebugLibraryKind.Dac && Path.IsPathRooted(l.FileName) && File.Exists(l.FileName))
            .Select(l => l.FileName)
            .FirstOrDefault();
}
