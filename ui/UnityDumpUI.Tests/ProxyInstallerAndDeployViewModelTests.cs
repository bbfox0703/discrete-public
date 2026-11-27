// ProxyInstallerAndDeployViewModelTests.cs — A-M9 + A-M10 regression tests.
//
// Covers:
//   * A-M9: WindowsProxyInstaller Detect/Install/Uninstall coexistence with
//     third-party proxy DLLs (ReShade/ENB/SpecialK version.dll or winmm.dll).
//     Only Discrete-owned DLLs count toward ProxyInstallStatus.Kind and the
//     mutex conflict check; foreign DLLs surface via ForeignProxyPresent,
//     stay untouched on Install/Uninstall, and are never mistaken for an
//     outdated Discrete install by UpdateAllDeployed.
//   * A-M10: ProxyDeployViewModel gates Refresh on `!IsScanning && !IsBusy`,
//     gates bulk operations on `!IsBusy && !IsScanning`, and enumerates a
//     snapshot (`Games.ToList()`) inside RunBulkAsync so concurrent mutations
//     never throw InvalidOperationException.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityDumpUI.Core.Platform;
using UnityDumpUI.Models;
using UnityDumpUI.Platform;
using UnityDumpUI.ViewModels;
using Xunit;

namespace UnityDumpUI.Tests;

public class ProxyInstallerAndDeployViewModelTests
{
    private sealed class TempProxySandbox : IDisposable
    {
        public string RootDir   { get; }
        public string SourceDir { get; }
        public string GameDir   { get; }
        public string GameExe   { get; }

        public TempProxySandbox()
        {
            RootDir   = Path.Combine(Path.GetTempPath(), "discrete_proxy_test_" + Guid.NewGuid().ToString("N"));
            SourceDir = Path.Combine(RootDir, "dist");
            GameDir   = Path.Combine(RootDir, "game");
            GameExe   = Path.Combine(GameDir, "TestGame.exe");
            Directory.CreateDirectory(SourceDir);
            Directory.CreateDirectory(GameDir);

            // Minimal dummy exe (not x86 PE -> falls back to x64 source dir).
            File.WriteAllText(GameExe, "MZ");

            // Populate dist/ with tagged Discrete source files.
            File.WriteAllText(Path.Combine(SourceDir, "version.dll"),          "DISCRETE:0.1.0.200");
            File.WriteAllText(Path.Combine(SourceDir, "winmm.dll"),            "DISCRETE:0.1.0.200");
            File.WriteAllText(Path.Combine(SourceDir, "Discrete-Mono.dll"),    "DISCRETE:0.1.0.200");
            File.WriteAllText(Path.Combine(SourceDir, "Discrete-IL2CPP.dll"),  "DISCRETE:0.1.0.200");
        }

        public WindowsProxyInstaller CreateInstaller() => new(
            sourceDirProvider: _ => SourceDir,
            isOurDll: path => File.Exists(path) && File.ReadAllText(path).StartsWith("DISCRETE:", StringComparison.Ordinal),
            readFileVersion: path =>
            {
                if (!File.Exists(path)) return null;
                string txt = File.ReadAllText(path);
                int colon = txt.IndexOf(':');
                return colon >= 0 ? txt.Substring(colon + 1) : null;
            });

        public void Dispose()
        {
            try { if (Directory.Exists(RootDir)) Directory.Delete(RootDir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public async Task Detect_ForeignVersionDllAlone_ReportsNotInstalledWithForeignMarker()
    {
        using var sb = new TempProxySandbox();
        File.WriteAllText(Path.Combine(sb.GameDir, "version.dll"), "RESHADE:5.9.0");

        var installer = sb.CreateInstaller();
        ProxyInstallStatus st = await installer.DetectAsync(
            sb.GameExe, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(ProxyKind.None, st.Kind);
        Assert.False(st.IsInstalled);
        Assert.Null(st.ErrorMessage);
        Assert.Null(st.InstalledProxyVersion);
        Assert.Equal("version.dll", st.ForeignProxyPresent);
        Assert.Equal("0.1.0.200", st.SourceProxyVersion);
    }

    [Fact]
    public async Task Install_WinmmAlongsideForeignVersionDll_SucceedsAndDetectsCoexistenceCleanly()
    {
        using var sb = new TempProxySandbox();
        string foreignVersion = Path.Combine(sb.GameDir, "version.dll");
        File.WriteAllText(foreignVersion, "RESHADE:5.9.0");

        var installer = sb.CreateInstaller();
        ProxyOperationResult res = await installer.InstallAsync(
            sb.GameExe, ProxyKind.Winmm, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(res.Ok, res.Message);
        // Foreign version.dll must remain completely untouched.
        Assert.Equal("RESHADE:5.9.0", File.ReadAllText(foreignVersion));

        // Post-op Detect must see OUR winmm.dll as current, with NO mutex error,
        // and surface the coexisting foreign version.dll separately.
        Assert.Equal(ProxyKind.Winmm, res.Status.Kind);
        Assert.Null(res.Status.ErrorMessage);
        Assert.True(res.Status.IsCurrent);
        Assert.Equal("0.1.0.200", res.Status.InstalledProxyVersion);
        Assert.Equal("version.dll", res.Status.ForeignProxyPresent);
    }

    [Fact]
    public async Task Install_VersionWhenForeignVersionExists_RefusesWithoutTouchingForeignFile()
    {
        using var sb = new TempProxySandbox();
        string foreignVersion = Path.Combine(sb.GameDir, "version.dll");
        File.WriteAllText(foreignVersion, "ENB:1.0.0");

        var installer = sb.CreateInstaller();
        ProxyOperationResult res = await installer.InstallAsync(
            sb.GameExe, ProxyKind.Version, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(res.Ok);
        Assert.Contains("not installed by Discrete", res.Message);
        Assert.Equal("ENB:1.0.0", File.ReadAllText(foreignVersion));
        Assert.Equal(ProxyKind.None, res.Status.Kind);
        Assert.Equal("version.dll", res.Status.ForeignProxyPresent);
    }

    [Fact]
    public async Task Detect_BothProxiesOwnedByDiscrete_ReportsMutexConflictError()
    {
        using var sb = new TempProxySandbox();
        File.WriteAllText(Path.Combine(sb.GameDir, "version.dll"), "DISCRETE:0.1.0.150");
        File.WriteAllText(Path.Combine(sb.GameDir, "winmm.dll"),   "DISCRETE:0.1.0.200");

        var installer = sb.CreateInstaller();
        ProxyInstallStatus st = await installer.DetectAsync(
            sb.GameExe, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(ProxyKind.None, st.Kind);
        Assert.NotNull(st.ErrorMessage);
        Assert.Contains("both Discrete version.dll and winmm.dll", st.ErrorMessage);
        Assert.Null(st.ForeignProxyPresent);
    }

    [Fact]
    public async Task Uninstall_CoexistingForeignVersionAndDiscreteWinmm_RemovesOnlyDiscreteFiles()
    {
        using var sb = new TempProxySandbox();
        string foreignVersion = Path.Combine(sb.GameDir, "version.dll");
        File.WriteAllText(foreignVersion, "SPECIALK:2.0.0");

        var installer = sb.CreateInstaller();
        var install = await installer.InstallAsync(
            sb.GameExe, ProxyKind.Winmm, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(install.Ok);

        var uninstall = await installer.UninstallAsync(
            sb.GameExe, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(uninstall.Ok);
        Assert.True(File.Exists(foreignVersion));
        Assert.Equal("SPECIALK:2.0.0", File.ReadAllText(foreignVersion));
        Assert.False(File.Exists(Path.Combine(sb.GameDir, "winmm.dll")));
        Assert.False(File.Exists(Path.Combine(sb.GameDir, "Discrete-Mono.dll")));
        Assert.Equal(ProxyKind.None, uninstall.Status.Kind);
        Assert.Equal("version.dll", uninstall.Status.ForeignProxyPresent);
    }

    private sealed class StubScanner : IUnityGameScanner
    {
        public Func<CancellationToken, Task<IReadOnlyList<UnityGameEntry>>> OnScan { get; set; } =
            _ => Task.FromResult<IReadOnlyList<UnityGameEntry>>(Array.Empty<UnityGameEntry>());

        public Task<IReadOnlyList<UnityGameEntry>> ScanAsync(CancellationToken ct = default) => OnScan(ct);
    }

    private sealed class StubInstaller : IProxyInstaller
    {
        public List<(string Exe, ProxyKind Kind)> InstallCalls { get; } = new();
        public Func<string, ProxyKind, Task<ProxyOperationResult>>? OnInstall { get; set; }

        public Task<ProxyInstallStatus> DetectAsync(string gameExePath, CancellationToken ct = default) =>
            Task.FromResult(new ProxyInstallStatus(gameExePath, ProxyKind.None, false, false, null));

        public Task<ProxyOperationResult> InstallAsync(string gameExePath, ProxyKind kind, CancellationToken ct = default)
        {
            InstallCalls.Add((gameExePath, kind));
            if (OnInstall is not null) return OnInstall(gameExePath, kind);
            var st = new ProxyInstallStatus(gameExePath, kind, true, true, null, "0.1.0.200", "0.1.0.200");
            return Task.FromResult(new ProxyOperationResult(true, "ok", st));
        }

        public Task<ProxyOperationResult> UninstallAsync(string gameExePath, CancellationToken ct = default)
        {
            var st = new ProxyInstallStatus(gameExePath, ProxyKind.None, false, false, null);
            return Task.FromResult(new ProxyOperationResult(true, "ok", st));
        }
    }

    [Fact]
    public async Task UpdateAllDeployed_SkipsForeignOnlyRow_AndUpdatesCoexistingWinmmRow()
    {
        var foreignOnly = new UnityGameEntry(
            @"C:\Games\A\A.exe", @"C:\Games\A", "GameA",
            UnityBackend.IL2CPP, ExeBitness.X64, "A_Data",
            installedProxy: ProxyKind.None, source: "Steam",
            installedProxyVersion: null, sourceProxyVersion: "0.1.0.200",
            foreignProxyPresent: "version.dll");

        var coexistingWinmm = new UnityGameEntry(
            @"C:\Games\B\B.exe", @"C:\Games\B", "GameB",
            UnityBackend.IL2CPP, ExeBitness.X64, "B_Data",
            installedProxy: ProxyKind.Winmm, source: "Steam",
            installedProxyVersion: "0.1.0.150", sourceProxyVersion: "0.1.0.200",
            foreignProxyPresent: "version.dll");

        var scanner = new StubScanner
        {
            OnScan = _ => Task.FromResult<IReadOnlyList<UnityGameEntry>>(new[] { foreignOnly, coexistingWinmm }),
        };
        var installer = new StubInstaller
        {
            OnInstall = (exe, kind) => Task.FromResult(new ProxyOperationResult(
                true, "updated",
                new ProxyInstallStatus(exe, kind, true, true, null, "0.1.0.200", "0.1.0.200", "version.dll"))),
        };

        var vm = new ProxyDeployViewModel(scanner, installer);
        await vm.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);

        Assert.Equal(2, vm.Games.Count);
        Assert.Equal(ProxyDeployStatus.NotDeployed, vm.Games[0].DeployStatus);
        Assert.Contains("[other: version.dll]", vm.Games[0].DeployStatusLabel);
        Assert.Equal(ProxyDeployStatus.DeployedOutdated, vm.Games[1].DeployStatus);
        Assert.Contains("[other: version.dll]", vm.Games[1].DeployStatusLabel);

        await vm.UpdateAllDeployedCommand.ExecuteAsync(null).ConfigureAwait(true);

        // Only GameB (our winmm.dll) should be touched; GameA (foreign version.dll only) is skipped.
        var call = Assert.Single(installer.InstallCalls);
        Assert.Equal(@"C:\Games\B\B.exe", call.Exe);
        Assert.Equal(ProxyKind.Winmm, call.Kind);
        Assert.Equal(ProxyDeployStatus.DeployedCurrent, vm.Games[1].DeployStatus);
        Assert.Contains("[other: version.dll]", vm.Games[1].DeployStatusLabel);
    }

    [Fact]
    public async Task RunBulkAsync_DisablesRefreshDuringBulk_AndSurvivesCollectionMutationViaSnapshot()
    {
        var g1 = new UnityGameEntry(
            @"C:\Games\G1\G1.exe", @"C:\Games\G1", "G1",
            UnityBackend.Mono, ExeBitness.X64, "G1_Data", ProxyKind.None, "Steam");
        var g2 = new UnityGameEntry(
            @"C:\Games\G2\G2.exe", @"C:\Games\G2", "G2",
            UnityBackend.Mono, ExeBitness.X64, "G2_Data", ProxyKind.None, "Steam");

        int scanCalls = 0;
        var scanner = new StubScanner
        {
            OnScan = _ =>
            {
                ++scanCalls;
                return Task.FromResult<IReadOnlyList<UnityGameEntry>>(new[] { g1, g2 });
            },
        };

        var enteredBulk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBulk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var installer = new StubInstaller
        {
            OnInstall = async (exe, kind) =>
            {
                if (exe.EndsWith("G1.exe", StringComparison.Ordinal))
                {
                    enteredBulk.TrySetResult();
                    await releaseBulk.Task.ConfigureAwait(false);
                }
                return new ProxyOperationResult(
                    true, "ok", new ProxyInstallStatus(exe, kind, true, true, null, "0.1.0.200", "0.1.0.200"));
            },
        };

        var vm = new ProxyDeployViewModel(scanner, installer);
        await vm.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(1, scanCalls);

        Task bulkTask = vm.InstallSelectedCommand.ExecuteAsync(null);
        await enteredBulk.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        // A-M10: Refresh must be disabled while bulk is running.
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanRefresh);
        Assert.False(vm.RefreshCommand.CanExecute(null));

        // Even if RefreshCommand is invoked directly while busy, it must no-op (not clear Games).
        await vm.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal(1, scanCalls);
        Assert.Equal(2, vm.Games.Count);

        // Even if Games is mutated externally mid-bulk, the snapshot prevents InvalidOperationException.
        vm.Games.Clear();
        releaseBulk.SetResult();
        await bulkTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(2, installer.InstallCalls.Count);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanRefresh);
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshAsync_DisablesBulkCommandsWhileScanning()
    {
        var enteredScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var scanner = new StubScanner
        {
            OnScan = async _ =>
            {
                enteredScan.TrySetResult();
                await releaseScan.Task.ConfigureAwait(false);
                return Array.Empty<UnityGameEntry>();
            },
        };
        var vm = new ProxyDeployViewModel(scanner, new StubInstaller());

        Task scanTask = vm.RefreshCommand.ExecuteAsync(null);
        await enteredScan.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(vm.IsScanning);
        Assert.False(vm.CanRefresh);
        Assert.False(vm.CanRunBulk);
        Assert.False(vm.InstallSelectedCommand.CanExecute(null));
        Assert.False(vm.UninstallSelectedCommand.CanExecute(null));
        Assert.False(vm.UpdateAllDeployedCommand.CanExecute(null));

        releaseScan.SetResult();
        await scanTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.False(vm.IsScanning);
        Assert.True(vm.CanRefresh);
        Assert.True(vm.CanRunBulk);
    }
}
