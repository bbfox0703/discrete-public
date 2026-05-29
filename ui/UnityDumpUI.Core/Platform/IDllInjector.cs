// IDllInjector.cs — platform abstraction for DLL injection.
//
// Mirror of tools/Inject-Dll.ps1 (CreateRemoteThread + LoadLibraryW). The UI
// uses this to load Discrete-<Backend>.dll into a target Unity process so
// the user no longer needs to drop to PowerShell. Real impl lives under
// UnityDumpUI/Platform; Core just declares the contract.

using System.Threading;
using System.Threading.Tasks;

namespace UnityDumpUI.Core.Platform;

public interface IDllInjector
{
    /// <summary>
    /// Inject <paramref name="dllPath"/> into the process identified by
    /// <paramref name="pid"/>. Returns success + the LoadLibraryW exit code
    /// (low 32 bits of the loaded HMODULE) or a human-readable error.
    /// </summary>
    Task<InjectResult> InjectAsync(int pid, string dllPath, CancellationToken ct = default);
}

public sealed record InjectResult(bool Ok, uint LoadLibraryExitCode, string? ErrorMessage)
{
    public static InjectResult Success(uint code)        => new(true,  code, null);
    public static InjectResult Failure(string message)   => new(false, 0,    message);
}
