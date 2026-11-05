namespace Tamp.Cli.Scaffold.Templates;

/// <summary>
/// The repository bootstrap that <c>tamp init</c> writes (<c>tamp.sh</c>, <c>tamp.cmd</c>,
/// <c>.config/dotnet-tools.json</c>). One copy, compiled into both Tamp.Cli and Tamp.Migrate
/// (linked source), so a migrated NUKE build gets byte-for-byte the scaffold a new one does,
/// including the line-ending rules tamp#120 settled.
/// </summary>
internal static class BootstrapFiles
{
    internal static string ToolsJson(string tampVersion)
        => $$"""
        {
          "version": 1,
          "isRoot": true,
          "tools": {
            "dotnet-tamp": {
              "version": "{{tampVersion}}",
              "commands": ["dotnet-tamp"]
            }
          }
        }

        """;

    /// <summary>
    /// POSIX shim. Restores the local tool manifest on first run so a fresh
    /// clone works without an explicit `dotnet tool restore`, then forwards
    /// args verbatim to <c>dotnet tamp</c>. Marker file under <c>.config/</c>
    /// avoids re-running restore on every invocation.
    /// </summary>
    /// <remarks>
    /// Always LF, whatever line endings this source file had when the CLI was
    /// compiled (a Windows checkout with <c>core.autocrlf</c> would otherwise
    /// bake CRLF into the raw literal and break bash). See tamp#120.
    /// </remarks>
    internal static string TampSh()
        => """
        #!/usr/bin/env bash
        # Tamp build-script entry point. Scaffolded by `tamp init` — safe to edit; safe to delete.
        # Restores the local tool manifest on first run, then forwards to `dotnet tamp <args>`.
        set -euo pipefail

        SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
        STAMP="$SCRIPT_DIR/.config/.tamp-tools-restored"

        if [ -f "$SCRIPT_DIR/.config/dotnet-tools.json" ] && [ ! -f "$STAMP" ]; then
            (cd "$SCRIPT_DIR" && dotnet tool restore) && mkdir -p "$SCRIPT_DIR/.config" && touch "$STAMP"
        fi

        cd "$SCRIPT_DIR" && exec dotnet tamp "$@"
        """.ReplaceLineEndings("\n");

    /// <summary>
    /// Windows shim (cmd.exe). Mirrors <see cref="TampSh"/> behavior so
    /// adopters on Windows have the same one-line entry point.
    /// </summary>
    /// <remarks>
    /// tamp#120: cmd.exe misparses batch files that are LF-only, especially when
    /// they also contain multi-byte characters under an OEM code page such as
    /// 437 (it loses its place and runs a fragment of a later line, e.g.
    /// <c>'m' is not recognized</c>). So this shim is always CRLF, normalized
    /// here because the raw literal carries the line endings of whatever
    /// checkout the CLI was built from (LF on the ubuntu release runner), and
    /// the text must stay pure ASCII. <c>%~dp0</c> always ends in <c>\</c>, so
    /// paths are joined directly onto it with no trim.
    /// </remarks>
    internal static string TampCmd()
        => """
        @echo off
        rem Tamp build-script entry point. Scaffolded by `tamp init` - safe to edit; safe to delete.
        rem Restores the local tool manifest on first run, then forwards to `dotnet tamp <args>`.
        setlocal

        set "SCRIPT_DIR=%~dp0"
        set "STAMP=%SCRIPT_DIR%.config\.tamp-tools-restored"

        if exist "%SCRIPT_DIR%.config\dotnet-tools.json" if not exist "%STAMP%" (
            pushd "%SCRIPT_DIR%" >nul
            dotnet tool restore || exit /b 1
            popd >nul
            if not exist "%SCRIPT_DIR%.config" mkdir "%SCRIPT_DIR%.config"
            type nul > "%STAMP%"
        )

        pushd "%SCRIPT_DIR%" >nul
        dotnet tamp %*
        set "EXIT=%ERRORLEVEL%"
        popd >nul
        exit /b %EXIT%

        """.ReplaceLineEndings("\r\n");
}
