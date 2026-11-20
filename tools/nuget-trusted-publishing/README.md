# nuget.org Trusted Publishing — bulk policy creation

All pre-2026-08-17 nuget.org API keys expired **2026-11-01**, and new keys cap at 30
days. The org secret every satellite release used is dead, so each repo has to move to
[trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
(OIDC) before it can publish again. That means **one policy per repo**, and nuget.org
offers **no API** for creating one — the Trusted Publishing page is the only way in.

With ~60 satellites all needing to release for the net11 TFM rollover, that is ~60 web
forms. This drives them.

## How it works

It attaches to a Chrome you already have open and signed in, over the DevTools
protocol. It never launches a browser, never handles credentials, and never touches a
tab other than the Trusted Publishing page.

```bash
# Chrome must be running with remote debugging and signed in to nuget.org:
#   chrome.exe --remote-debugging-port=9222
# then open https://www.nuget.org/account/trustedpublishing

npm install

node list.mjs                                        # what already exists
node create.mjs --repos tamp-trivy --dry-run         # fill + verify + cancel
node create.mjs --repos tamp-trivy,tamp-zap          # create
node create.mjs --file repos-unconverted.txt         # or work from a list
```

`TP_CDP` overrides the endpoint (default `http://127.0.0.1:9222`). Pick the port of the
browser that holds the nuget.org session — if several Chromes expose CDP, the others
are somebody's actual browsing and should be left alone.

## What it writes

Every policy is created with the configuration proven by the repos already publishing
this way:

| Field | Value |
| --- | --- |
| Policy name | the repository name |
| Package owner | **Tamp** (the org holding the reserved `Tamp.*` prefix — *not* a personal account) |
| CI/CD provider | GitHub Actions |
| Repository owner | `tamp-build` |
| Workflow file | `release.yml` |
| Environment | `release` |
| Scopes | Push → *Push new packages and package versions* |
| Glob | `*` |

The **environment** is load-bearing: the publish job declares `environment: release`,
and a policy that omits it will not match the OIDC claim. The **glob is required** —
nuget.org rejects a policy without one (*"At least one valid glob pattern or package
must be specified"*).

## Why it is this paranoid

**A policy's repository cannot be edited after creation.** A typo is permanent: this
account already carries a dead policy pointing at `tamp-pnmp`, a repository that has
never existed, created by hand. So:

1. every repo is confirmed to exist on GitHub before the form is opened;
2. every repo is confirmed to have `.github/workflows/release.yml` — a policy naming a
   workflow that isn't there can never match;
3. repos that already have a policy are skipped rather than duplicated;
4. after filling, **every field is read back and compared to intent**, and any
   mismatch aborts the run *before* submitting;
5. after submitting, the policy is re-read from the Manage panel and verified. The list
   is Knockout-rendered and lags the navigation, so this polls — reading once makes a
   successful create look like a failed one.

Steps 1 and 2 are the ones that matter. The rest only protect against a nuget.org
redesign; those two protect against the mistake that actually happened.

## Creating in waves, not all at once

A new policy is **temporary for 7 days** and goes dormant unless a publish uses it
("Activate for 7 days" reappears on the row). Creating all 60 up front would just mean
re-activating 60 by hand later. Create a wave's policies immediately before that wave's
releases.

Per repo the full sequence is:

1. policy (this tool)
2. workflow conversion commit — job-scoped `id-token: write`, `NuGet/login` pinned by
   SHA, `NUGET_API_KEY` from its step output
3. `Tamp.*` pin bump **and** TFM swap in one commit (NU1202 otherwise)
4. release, verify on NuGet

`repos-unconverted.txt` is the remaining set: repos whose `release.yml` still has no
`NuGet/login` step.
