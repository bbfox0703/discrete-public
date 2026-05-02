namespace UnityDumpUI.Core.Platform;

/// <summary>
/// Detected Unity scripting backend in a target process. <c>None</c> means
/// neither GameAssembly.dll nor a mono runtime DLL was found loaded — most
/// likely not a Unity game (or the modules are unreadable from our perms).
/// </summary>
public enum UnityBackend { None, Mono, IL2CPP }

/// <summary>
/// Platform abstraction for enumerating processes. Real implementation lives in
/// UnityDumpUI (Windows-specific); Core only declares the interface.
/// </summary>
public interface IProcessEnumerator
{
    /// <summary>Every visible process; <see cref="ProcessInfo.Backend"/> is best-effort.</summary>
    IReadOnlyList<ProcessInfo> List();

    /// <summary>Subset of <see cref="List"/> filtered to processes with a detected Unity backend.</summary>
    IReadOnlyList<ProcessInfo> ListUnityProcesses();
}

public sealed record ProcessInfo(
    int           Pid,
    string        Name,
    string?       Path,
    UnityBackend  Backend = UnityBackend.None,
    bool          IsWow64 = false);
