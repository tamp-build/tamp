using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Tamp;

/// <summary>
/// Abstraction over the host's OS-level secret store. Implementations
/// are platform-specific; <see cref="OsSecretStore.Detect"/> returns the
/// right one for the current host, or <c>null</c> when no store is
/// available.
/// </summary>
/// <remarks>
/// <para>
/// Tamp stores entries under a fixed service / target name of
/// <c>tamp</c>, with the account / target-component being the secret's
/// resolved name (the <see cref="SecretAttribute.EnvironmentVariable"/>
/// override or <c>UPPER_SNAKE_CASE</c> of the member). Users add
/// entries via their platform's native tool:
/// </para>
/// <list type="bullet">
///   <item>macOS: <c>security add-generic-password -s tamp -a NUGET_API_KEY -w "ghp_xxx"</c></item>
///   <item>Linux: <c>echo "ghp_xxx" | secret-tool store --label="Tamp NUGET_API_KEY" service tamp account NUGET_API_KEY</c></item>
///   <item>Windows: <c>cmdkey /generic:tamp:NUGET_API_KEY /user:tamp /pass:ghp_xxx</c></item>
/// </list>
/// </remarks>
public interface IOsSecretStore
{
    /// <summary>
    /// Attempt to retrieve the value for <paramref name="name"/>.
    /// Returns <c>null</c> when no entry exists OR when the store
    /// reports any other failure (missing tool, locked keychain, etc.)
    /// — the binder treats null as "skip this leg, continue the chain".
    /// </summary>
    string? TryGet(string name);
}

/// <summary>Platform detection for <see cref="IOsSecretStore"/>.</summary>
public static class OsSecretStore
{
    /// <summary>
    /// The fixed service / target name Tamp uses across all platforms.
    /// Keeps the user's keychain organized and avoids collisions with
    /// other apps storing under the same account name.
    /// </summary>
    public const string ServiceName = "tamp";

    /// <summary>
    /// Returns the platform's store, or <c>null</c> when none is
    /// detected (an exotic OS, or the necessary CLI / API is missing).
    /// </summary>
    public static IOsSecretStore? Detect()
    {
        if (OperatingSystem.IsMacOS()) return new MacOsKeychainStore();
        if (OperatingSystem.IsLinux()) return new LinuxSecretToolStore();
        if (OperatingSystem.IsWindows()) return new WindowsCredentialManagerStore();
        return null;
    }
}

/// <summary>
/// macOS Keychain backend via the <c>security</c> CLI
/// (preinstalled on every macOS host). Read-only.
/// </summary>
internal sealed class MacOsKeychainStore : IOsSecretStore
{
    public string? TryGet(string name)
    {
        // security find-generic-password -s tamp -a <name> -w
        // -w prints just the password value to stdout.
        // Exit non-zero when the entry isn't found.
        var psi = new ProcessStartInfo("security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("find-generic-password");
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(OsSecretStore.ServiceName);
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add(name);
        psi.ArgumentList.Add("-w");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0) return null;
            // -w produces the value followed by a trailing newline.
            var value = stdout.TrimEnd('\n', '\r');
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Linux backend via <c>secret-tool</c> (libsecret-tools package).
/// Returns null silently if the tool isn't installed — Tamp doesn't
/// require it.
/// </summary>
internal sealed class LinuxSecretToolStore : IOsSecretStore
{
    public string? TryGet(string name)
    {
        // secret-tool lookup service tamp account <name>
        // Exit 0 with the value on stdout when found; exit 1 (no output) when not found;
        // exit 127 / "command not found" when libsecret-tools isn't installed.
        var psi = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("lookup");
        psi.ArgumentList.Add("service");
        psi.ArgumentList.Add(OsSecretStore.ServiceName);
        psi.ArgumentList.Add("account");
        psi.ArgumentList.Add(name);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0) return null;
            // secret-tool emits the value with no trailing newline, but
            // trim defensively.
            var value = stdout.TrimEnd('\n', '\r');
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Windows Credential Manager backend via P/Invoke to
/// <c>Advapi32.CredReadW</c>. The target name is
/// <c>tamp:&lt;name&gt;</c>; the credential blob holds the value as
/// UTF-16. Users add entries via:
/// <c>cmdkey /generic:tamp:NUGET_API_KEY /user:tamp /pass:ghp_xxx</c>
/// (/generic writes a CRED_TYPE_GENERIC entry CredRead can read; /add is for
/// domain creds and won't round-trip here. cmdkey writes but can't read; Tamp reads via CredRead).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCredentialManagerStore : IOsSecretStore
{
    private const int CRED_TYPE_GENERIC = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
    private static extern void CredFree(IntPtr cred);

    public string? TryGet(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;  // safety net

        var target = $"{OsSecretStore.ServiceName}:{name}";
        var ok = CredRead(target, CRED_TYPE_GENERIC, 0, out var credPtr);
        if (!ok || credPtr == IntPtr.Zero) return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return null;
            // CredentialBlob is opaque bytes. cmdkey /pass stores the value
            // as UTF-16 LE. Other writers (e.g. .NET's own helpers) may
            // use UTF-8. Try UTF-16 first; if the result looks corrupted
            // (interleaved nulls suggest UTF-8 misread as UTF-16, etc.),
            // fall back to UTF-8.
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);
            var asUtf16 = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            if (LooksReasonable(asUtf16)) return string.IsNullOrEmpty(asUtf16) ? null : asUtf16;
            var asUtf8 = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            return string.IsNullOrEmpty(asUtf8) ? null : asUtf8;
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    // Heuristic: a UTF-16 decode of UTF-8 bytes alternates real chars
    // with nulls or replacement chars. If the string contains no
    // control chars / replacements and the length looks proportional,
    // accept it.
    private static bool LooksReasonable(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
        {
            if (c == '�') return false;            // replacement char = decode failed
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') return false;
        }
        return true;
    }
}
