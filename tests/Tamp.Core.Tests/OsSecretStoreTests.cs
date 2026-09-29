using Xunit;

namespace Tamp.Core.Tests;

/// <summary>
/// Tests the OS-level secret store backends (<see cref="IOsSecretStore"/>).
/// Each backend shells out to its platform's native tool (macOS <c>security</c>,
/// Linux <c>secret-tool</c>) or P/Invokes CredRead (Windows). The happy path
/// needs the real tool, so these tests assert the "no entry present" contract
/// (every backend returns <c>null</c> for a missing key) which exercises each
/// store's real code path on its own CI-matrix leg, and the safety-net / not-found
/// catch branches on the other legs. Between the three OS legs, every branch is hit.
/// </summary>
public sealed class OsSecretStoreTests
{
    // A name that will never exist in any developer's or CI runner's store.
    private static readonly string MissingKey = "TAMP_TEST_NONEXISTENT_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void ServiceName_Is_Tamp()
        => Assert.Equal("tamp", OsSecretStore.ServiceName);

    [Fact]
    public void Detect_Returns_The_Platform_Store()
    {
        var store = OsSecretStore.Detect();
        if (OperatingSystem.IsMacOS())
            Assert.Equal("MacOsKeychainStore", store?.GetType().Name);
        else if (OperatingSystem.IsLinux())
            Assert.Equal("LinuxSecretToolStore", store?.GetType().Name);
        else if (OperatingSystem.IsWindows())
            Assert.Equal("WindowsCredentialManagerStore", store?.GetType().Name);
        else
            Assert.Null(store); // exotic OS → no store
    }

    [Fact]
    public void Detected_Store_Returns_Null_For_A_Missing_Entry()
    {
        var store = OsSecretStore.Detect();
        // On every supported OS a store is detected; a missing key yields null.
        if (store is not null)
            Assert.Null(store.TryGet(MissingKey));
    }

    [Fact]
    public void Windows_Store_Returns_Null_For_Missing_Entry()
    {
        // Drives the CredRead miss path on Windows. The type is [SupportedOSPlatform("windows")],
        // so it's only referenced under an IsWindows() guard (CA1416); the CI Windows leg covers it.
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsCredentialManagerStore();
        Assert.Null(store.TryGet(MissingKey));
    }

    [Fact]
    public void Windows_Store_Reads_Back_A_Value_Written_Via_Cmdkey()
    {
        // Covers the real decode path (Marshal + UTF-16 decode + LooksReasonable) — the
        // part the "missing entry" test can't reach. Writes a throwaway GENERIC credential
        // exactly as the class documents (cmdkey /add:tamp:<name> ...), reads it, cleans up.
        if (!OperatingSystem.IsWindows()) return;

        var name = MissingKey;                 // unique per run
        var target = $"{OsSecretStore.ServiceName}:{name}";
        const string value = "ghp_roundtrip_value_123";

        // NOTE: generic credentials (what CredRead(CRED_TYPE_GENERIC) reads) are written with
        // /generic:, not /add: (which is for domain creds and rejects /pass).
        var added = Cmdkey($"/generic:{target}", "/user:tamp", $"/pass:{value}");
        if (!added) return;                    // cmdkey unavailable/blocked → skip, don't fail
        try
        {
            var store = new WindowsCredentialManagerStore();
            Assert.Equal(value, store.TryGet(name));
        }
        finally
        {
            Cmdkey($"/delete:{target}");
        }
    }

    private static bool Cmdkey(params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmdkey")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return false;
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    [Fact]
    public void MacOs_Store_Returns_Null_For_Missing_Entry()
    {
        // On macOS this runs `security find-generic-password` (exit != 0 → null).
        // Off macOS `security` isn't found → the catch returns null. No throw either way.
        var store = new MacOsKeychainStore();
        Assert.Null(store.TryGet(MissingKey));
    }

    [Fact]
    public void Linux_Store_Returns_Null_For_Missing_Entry()
    {
        // On Linux this runs `secret-tool lookup` (exit != 0 → null); if libsecret-tools
        // isn't installed the catch returns null. Off Linux, tool-not-found → catch → null.
        var store = new LinuxSecretToolStore();
        Assert.Null(store.TryGet(MissingKey));
    }
}
