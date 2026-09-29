# ADR 0024: OS-native credential store as a secret-resolution leg

* Status: Accepted
* Date: 2026-09-29
* Deciders: scott
* Tracking: [#87](https://github.com/tamp-build/tamp/issues/87)

> Retroactive record. `OsSecretStore` shipped in `Tamp.Core` (with the `SecretBinder` chain) but the decision was never captured as an ADR; the `tamp-conformance` reverse-examination dogfood flagged it as an undocumented architectural decision. This ADR records what was decided and why.

## Context and Problem Statement

[ADR 0005](0005-secret-distinct-type.md) established `Secret` as a distinct type, but not *where a secret's value comes from* at bind time. A build declares `[Secret(EnvironmentVariable = "NUGET_API_KEY")]`; the framework has to resolve that to a value before the target runs.

Environment variables are the obvious source, but they are a poor primary store for local development: they leak into child-process environments and shell history, they are awkward to set per-project, and encouraging developers to `export NUGET_API_KEY=…` trains exactly the habit the `Secret` type exists to discourage. Developers already have a **secure, OS-managed credential store** — macOS Keychain, the Linux Secret Service (libsecret), Windows Credential Manager. The framework should read from it.

## Decision Drivers

* **Don't make env vars the only local option.** A secret in an OS keychain is encrypted at rest and access-controlled by the OS; a secret in an env var is neither.
* **Cross-platform without a native dependency.** Tamp.Core must stay dependency-light and must not P/Invoke a platform crypto API from the core. Shelling to the platform's own CLI (`security`, `secret-tool`, `cmdkey`/`CredRead`) is portable and adds no package dependency.
* **Fail-soft.** A missing keychain tool (e.g. `libsecret-tools` not installed) or a locked keychain must not break the build — it should just fall through to the next resolution source.
* **Zero config for the common case.** A fixed, well-known service name means a developer stores a secret once with the native tool and every tamp build on that machine finds it.

## Considered Options

1. **Environment variables only.** Simplest, but trains bad habits and offers no at-rest protection locally.
2. **A Tamp-owned encrypted file (e.g. a DPAPI/age-encrypted vault in the repo or home dir).** Yet another secret store to manage, back up, and secure; reinvents what the OS already provides.
3. **Read from the OS-native credential store as a resolution leg, via each platform's CLI (chosen).** Uses the store developers already trust; no new dependency; degrades cleanly.

## Decision

`Tamp.Core` defines `IOsSecretStore` with a single `string? TryGet(string name)`, and `OsSecretStore.Detect()` returns the right implementation for the host:

* **macOS** — `MacOsKeychainStore`, via `security find-generic-password`.
* **Linux** — `LinuxSecretToolStore`, via `secret-tool lookup` (libsecret-tools).
* **Windows** — `WindowsCredentialManagerStore`, writing via `cmdkey` and reading via `CredRead`.

All entries live under a fixed service/target name **`tamp`** (`OsSecretStore.ServiceName`); the account key is the secret's resolved name (the `[Secret(EnvironmentVariable = …)]` override, else `UPPER_SNAKE_CASE` of the member). Developers add entries with their platform's native tool — e.g. `security add-generic-password -s tamp -a NUGET_API_KEY -w …`.

`SecretBinder` uses the store as **one leg in the resolution chain**: it calls `OsSecretStore.Detect()` and, for each secret, `osStore.TryGet(envKey)`. `TryGet` returns `null` on *any* failure — no entry, missing CLI, locked keychain, non-zero exit — and the binder treats `null` as "skip this leg, continue the chain." The OS store is a source, never a hard requirement.

## Consequences

* **Positive**: local secrets can live in the OS keychain instead of env vars / files — encrypted at rest, OS-access-controlled, set once per machine. The `Secret` type's intent (ADR 0005) is reinforced by a first-class secure source.
* **Positive**: no new package dependency and no native crypto P/Invoke in core — just the platform's own CLI. An exotic OS with no store returns `null` from `Detect()` and the chain falls through.
* **Positive**: fail-soft by construction — a missing tool or locked store never fails a build; it just isn't a contributing source.
* **Negative**: reading via a child process is slower than an in-proc API, and the value crosses a process boundary (the platform CLI's stdout) — acceptable for build-time secret resolution, and the value still lands in a `Secret`, never a bare string on the public surface.
* **Negative**: three platform code paths to maintain, each coupled to a CLI's output format (`cmdkey` notably can write but not read, so Windows reads via `CredRead`).

## Notes

This is the *store/binding* side of secrets; [ADR 0005](0005-secret-distinct-type.md) is the *type* side. The resolution chain order (CLI args / env / OS store / interactive prompt / future credential providers) lives in `SecretBinder`; this ADR fixes only that the OS-native store is a null-tolerant leg of it.
