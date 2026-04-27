namespace UnityDumpUI.Core.Platform;

/// <summary>
/// Platform abstraction for enumerating processes. Real implementation lives in
/// UnityDumpUI (Windows-specific); Core only declares the interface.
/// </summary>
public interface IProcessEnumerator
{
    IReadOnlyList<ProcessInfo> List();
}

public sealed record ProcessInfo(int Pid, string Name, string? Path);
