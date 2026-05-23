# Tamp.OpenGrep

> Tamp wrapper for the `opengrep` SAST CLI. Pattern-matching analyzer forked from Semgrep with multi-vendor governance (no Pro paywall). Emits SARIF by default so downstream sinks like `Tamp.DefectDojo.V2` consume it directly.

| Package | Status |
|---|---|
| `Tamp.OpenGrep` | Wave 1 |

## Install

`opengrep` is **not** in any of the usual package-manager registries — no winget manifest, no scoop bucket, no Homebrew formula, no PyPI package, no NuGet tool. The only supported install path is downloading the high-level CLI from [the project's GitHub Releases](https://github.com/opengrep/opengrep/releases/latest).

### Picking the right asset

The release artifacts split into two binary families. **You want the `opengrep_*` family** (the high-level CLI), not `opengrep-core_*` (the low-level OCaml engine). Tamp.OpenGrep wraps the high-level CLI.

| Platform | Asset |
|---|---|
| Linux x86_64 (glibc) | `opengrep_manylinux_x86` |
| Linux aarch64 (glibc) | `opengrep_manylinux_aarch64` |
| Linux x86_64 (musl) | `opengrep_musllinux_x86` |
| Linux aarch64 (musl) | `opengrep_musllinux_aarch64` |
| macOS x86_64 | `opengrep_osx_x86` |
| macOS arm64 (Apple Silicon) | `opengrep_osx_arm64` |
| Windows x64 | `opengrep_windows_x86.exe` |

Each artifact ships with a matching `.sig` (Sigstore signature) and `.cert` (signing certificate) for verification — see [Verifying signatures](#verifying-signatures-optional) below.

### Linux / macOS — one-liner

```bash
# Adjust ASSET for your platform from the table above.
ASSET=opengrep_osx_arm64
curl -L -o /usr/local/bin/opengrep \
  "https://github.com/opengrep/opengrep/releases/latest/download/${ASSET}"
chmod +x /usr/local/bin/opengrep
opengrep --version
```

### Windows — one-liner (PowerShell)

```powershell
$dest = "$env:LOCALAPPDATA\opengrep\opengrep.exe"
New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
Invoke-WebRequest `
  -Uri "https://github.com/opengrep/opengrep/releases/latest/download/opengrep_windows_x86.exe" `
  -OutFile $dest
# Add the install dir to PATH for the current user (one-time).
[Environment]::SetEnvironmentVariable(
  "PATH",
  [Environment]::GetEnvironmentVariable("PATH","User") + ";$(Split-Path $dest)",
  "User")
# Open a new shell, then:
opengrep --version
```

Tamp.OpenGrep emits `CommandPlan { Executable = "opengrep", ... }` — the binary must be on PATH or invoked via a wrapping `Tool` injected by the adopter.

### Verifying signatures (optional)

Releases are signed with Sigstore. To verify the binary you downloaded:

```bash
# Install cosign (https://docs.sigstore.dev/system_config/installation/).
ASSET=opengrep_osx_arm64
curl -L -o "${ASSET}"      "https://github.com/opengrep/opengrep/releases/latest/download/${ASSET}"
curl -L -o "${ASSET}.sig"  "https://github.com/opengrep/opengrep/releases/latest/download/${ASSET}.sig"
curl -L -o "${ASSET}.cert" "https://github.com/opengrep/opengrep/releases/latest/download/${ASSET}.cert"

cosign verify-blob \
  --certificate "${ASSET}.cert" \
  --signature "${ASSET}.sig" \
  --certificate-identity-regexp 'https://github.com/opengrep/opengrep/.*' \
  --certificate-oidc-issuer 'https://token.actions.githubusercontent.com' \
  "${ASSET}"
```

## Quick start

```csharp
using Tamp;
using Tamp.OpenGrep;

class Build : TampBuild
{
    public static int Main(string[] args) => Execute<Build>(args);

    [FromPath("opengrep")] readonly Tool OpenGrep = null!;

    Target Sast => _ => _.Executes(() => OpenGrep.Scan(s => s
        .AddRulePack("auto")
        .AddTarget("src")
        .EmitSarif("artifacts/opengrep.sarif")
        .DisableVersionCheck()
        .Quiet()));
}
```

## Why no auto-bootstrap

Tamp's install-source attributes (`[FromPath]`, `[FromNodeModules]`, `[NuGetPackage]`) cover the registries adopters typically reach for. `opengrep` doesn't currently distribute through any of them — it ships exclusively as signed binaries on GitHub Releases. If `opengrep` later publishes to winget / scoop / Homebrew / PyPI / NuGet, this README will be updated and (optionally) the wrapper can grow a matching install attribute.

## License

MIT — see the [LICENSE](../../LICENSE) at the repo root.
