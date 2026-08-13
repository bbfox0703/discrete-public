// Headless smoke harness for audit #4 items B-4 / B-4b (ProxyInstaller
// ownership checks). Drives the real WindowsProxyInstaller against SYNTHETIC
// game folders in the scratchpad — never a real game install.
//
// Fake games are 32-bit PEs on purpose: WindowsProxyInstaller.SourceDir() then
// reads from <BaseDir>\x86\, so our proxy version.dll never lands next to this
// harness exe (where the CLR would load it as a real dependency).

using System.Security.Cryptography;
using UnityDumpUI.Core.Platform;
using UnityDumpUI.Platform;

static class Program
{
    static int _pass, _fail;
    static readonly string Root = Path.Combine(
        Path.GetTempPath(), "discrete-proxysmoke");

    static string BaseDir()
    {
        string? d = Environment.ProcessPath is { Length: > 0 } pp
            ? Path.GetDirectoryName(pp) : null;
        return string.IsNullOrEmpty(d) ? AppContext.BaseDirectory : d;
    }

    static string Sha(string p)
    {
        using var s = File.OpenRead(p);
        return Convert.ToHexString(SHA256.HashData(s))[..16];
    }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {name}   {detail}"); }
    }

    static string NewCase(string name)
    {
        string d = Path.Combine(Root, name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }

    static async Task<int> Main()
    {
        const string dist86 = @"D:\Github\discrete\dist\x86";
        string stage = Path.Combine(BaseDir(), "x86");
        Directory.CreateDirectory(stage);
        foreach (string f in new[] { "version.dll", "winmm.dll", "Discrete-Mono.dll" })
            File.Copy(Path.Combine(dist86, f), Path.Combine(stage, f), true);
        Console.WriteLine($"harness exe dir : {BaseDir()}");
        Console.WriteLine($"source (x86)    : {stage}");
        Console.WriteLine($"synthetic games : {Root}");

        string fakeExeSrc = Path.Combine(dist86, "inject-x86.exe");   // real 32-bit PE
        string sysVersion = @"C:\Windows\SysWOW64\version.dll";       // 'third-party' stand-in
        string sysWinmm = @"C:\Windows\SysWOW64\winmm.dll";
        foreach (string f in new[] { fakeExeSrc, sysVersion, sysWinmm })
            if (!File.Exists(f)) { Console.WriteLine($"missing prerequisite: {f}"); return 2; }

        var inst = new WindowsProxyInstaller();

        // ---------------- Case A: foreign version.dll in the slot ----------
        Console.WriteLine("\n[A] game folder already has a THIRD-PARTY version.dll");
        string a = NewCase("A-foreign-version");
        string aExe = Path.Combine(a, "game.exe");
        File.Copy(fakeExeSrc, aExe);
        string aForeign = Path.Combine(a, "version.dll");
        File.Copy(sysVersion, aForeign);
        string foreignHash = Sha(aForeign);
        // pretend a previous Discrete install left its backend behind
        File.Copy(Path.Combine(dist86, "Discrete-Mono.dll"), Path.Combine(a, "Discrete-Mono.dll"));

        var r = await inst.InstallAsync(aExe, ProxyKind.Version);
        Console.WriteLine($"    install msg: {r.Message}");
        Check("A1 install refused", !r.Ok, $"Ok={r.Ok}");
        Check("A1 message names the cause",
            r.Message.Contains("not installed by Discrete", StringComparison.OrdinalIgnoreCase)
            && r.Message.Contains("Refusing to overwrite", StringComparison.OrdinalIgnoreCase));
        Check("A1 foreign dll untouched", File.Exists(aForeign) && Sha(aForeign) == foreignHash);

        r = await inst.UninstallAsync(aExe);
        Console.WriteLine($"    uninstall msg: {r.Message}");
        Check("A2 uninstall reports ok", r.Ok, $"Ok={r.Ok}");
        Check("A2 foreign dll survived", File.Exists(aForeign) && Sha(aForeign) == foreignHash);
        Check("A2 our backend removed", !File.Exists(Path.Combine(a, "Discrete-Mono.dll")));
        Check("A2 message says what was skipped",
            r.Message.Contains("version.dll", StringComparison.Ordinal)
            && r.Message.Contains("not installed by Discrete", StringComparison.OrdinalIgnoreCase));

        // ---------------- Case B: our own files round-trip -----------------
        Console.WriteLine("\n[B] our own proxy: install / reinstall / switch kind / uninstall");
        string b = NewCase("B-ours");
        string bExe = Path.Combine(b, "game.exe");
        File.Copy(fakeExeSrc, bExe);

        r = await inst.InstallAsync(bExe, ProxyKind.Version);
        Console.WriteLine($"    install msg: {r.Message}");
        Check("B1 install ok", r.Ok, r.Message);
        Check("B1 files present", File.Exists(Path.Combine(b, "version.dll"))
            && File.Exists(Path.Combine(b, "Discrete-Mono.dll")));
        Check("B1 detect reports Version", r.Status.Kind == ProxyKind.Version, r.Status.Kind.ToString());
        Check("B1 x86 install skips IL2CPP backend",
            !File.Exists(Path.Combine(b, "Discrete-IL2CPP.dll")));

        r = await inst.InstallAsync(bExe, ProxyKind.Version);
        Check("B2 reinstall over our own file ok", r.Ok, r.Message);

        r = await inst.InstallAsync(bExe, ProxyKind.Winmm);
        Console.WriteLine($"    switch msg: {r.Message}");
        Check("B3 switch to winmm ok", r.Ok, r.Message);
        Check("B3 winmm present", File.Exists(Path.Combine(b, "winmm.dll")));
        Check("B3 our version.dll removed (mutex)", !File.Exists(Path.Combine(b, "version.dll")));

        r = await inst.UninstallAsync(bExe);
        Console.WriteLine($"    uninstall msg: {r.Message}");
        Check("B4 uninstall ok", r.Ok, r.Message);
        Check("B4 folder clean", !File.Exists(Path.Combine(b, "winmm.dll"))
            && !File.Exists(Path.Combine(b, "version.dll"))
            && !File.Exists(Path.Combine(b, "Discrete-Mono.dll")));
        Check("B4 no skip note", !r.Message.Contains("Left ", StringComparison.Ordinal), r.Message);

        // ---------------- Case C: foreign DLL in the OTHER slot ------------
        Console.WriteLine("\n[C] third-party winmm.dll present, we install the version proxy");
        string c = NewCase("C-foreign-other-slot");
        string cExe = Path.Combine(c, "game.exe");
        File.Copy(fakeExeSrc, cExe);
        string cForeign = Path.Combine(c, "winmm.dll");
        File.Copy(sysWinmm, cForeign);
        string cHash = Sha(cForeign);

        r = await inst.InstallAsync(cExe, ProxyKind.Version);
        Console.WriteLine($"    install msg: {r.Message}");
        Check("C1 install ok (other slot is foreign, not ours)", r.Ok, r.Message);
        Check("C1 foreign winmm untouched", File.Exists(cForeign) && Sha(cForeign) == cHash);

        r = await inst.InstallAsync(cExe, ProxyKind.Winmm);
        Console.WriteLine($"    winmm install msg: {r.Message}");
        Check("C2 installing INTO the foreign slot refused", !r.Ok, $"Ok={r.Ok}");
        Check("C2 foreign winmm still untouched", File.Exists(cForeign) && Sha(cForeign) == cHash);

        r = await inst.UninstallAsync(cExe);
        Console.WriteLine($"    uninstall msg: {r.Message}");
        Check("C3 uninstall keeps foreign winmm", File.Exists(cForeign) && Sha(cForeign) == cHash);
        Check("C3 our files removed", !File.Exists(Path.Combine(c, "version.dll"))
            && !File.Exists(Path.Combine(c, "Discrete-Mono.dll")));
        Check("C3 message lists the skip",
            r.Message.Contains("winmm.dll", StringComparison.Ordinal), r.Message);

        // ---------------- Case D: detect on a folder we never touched ------
        Console.WriteLine("\n[D] detect only");
        string d = NewCase("D-detect");
        string dExe = Path.Combine(d, "game.exe");
        File.Copy(fakeExeSrc, dExe);
        File.Copy(sysVersion, Path.Combine(d, "version.dll"));
        var st = await inst.DetectAsync(dExe);
        Console.WriteLine($"    detect: kind={st.Kind} installedVer={st.InstalledProxyVersion ?? "(null)"} " +
                          $"sourceVer={st.SourceProxyVersion ?? "(null)"} isCurrent={st.IsCurrent}");
        Check("D1 foreign dll is not reported as current", !st.IsCurrent);

        Console.WriteLine($"\n==== {_pass} passed, {_fail} failed ====");
        return _fail == 0 ? 0 : 1;
    }
}
