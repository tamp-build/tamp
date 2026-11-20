/*
 * Create nuget.org Trusted Publishing policies for tamp-build satellites.
 *
 * Drives the signed-in Chrome already on the Trusted Publishing page over CDP
 * (nuget.org exposes no API for this). Connects to an EXISTING browser; it
 * never launches one and never touches another tab.
 *
 *   node create.mjs --repos tamp-sonar,tamp-trivy --dry-run
 *   node create.mjs --repos tamp-sonar,tamp-trivy
 *   node create.mjs --file wave1.txt
 *
 * The repository field is NOT editable once a policy exists, so a typo burns a
 * policy permanently (see the existing "tamp pub" -> tamp-pnmp). Guards:
 *   1. every repo is confirmed to exist on GitHub before the form is opened;
 *   2. every repo is confirmed to have .github/workflows/release.yml;
 *   3. repos that already have a policy are skipped, not duplicated;
 *   4. after filling, every field is read back and compared to intent --
 *      ANY mismatch aborts the whole run before submitting;
 *   5. after submitting, the new policy is re-read from the Manage panel and
 *      verified; a bad read aborts the run rather than continuing.
 */
import { readFileSync } from 'node:fs';
import { attach, listPolicies } from './lib.mjs';

const POLICY = {
  owner: 'Tamp',                 // the org holding the reserved Tamp.* prefix
  provider: 'GitHubActions',
  repositoryOwner: 'tamp-build',
  workflowFile: 'release.yml',
  environment: 'release',        // the publish job's environment: -- omit and the OIDC claim will not match
  glob: '*',                     // REQUIRED -- nuget.org rejects a policy with no glob
                                 // ("At least one valid glob pattern or package must be specified").
                                 // '*' == all packages, matching the core tamp policy; every package
                                 // these repos push is a reserved-prefix Tamp.* ID owned by Tamp.
};

const argv = process.argv.slice(2);
const flag = (n) => argv.includes(n);
const val = (n) => { const i = argv.indexOf(n); return i >= 0 ? argv[i + 1] : null; };
const DRY = flag('--dry-run');

let repos = [];
if (val('--repos')) {
  repos = val('--repos').split(',').map((s) => s.trim()).filter(Boolean);
} else if (val('--file')) {
  repos = readFileSync(val('--file'), 'utf8').split('\n').map((s) => s.trim()).filter((s) => s && !s.startsWith('#'));
}
if (!repos.length) {
  console.error('Nothing to do: pass --repos a,b or --file list.txt');
  process.exit(2);
}

const die = (msg) => { console.error('\nABORT: ' + msg); process.exit(1); };

// ---- Guard 1 + 2: the repo exists, and it has the workflow the policy names.
// Straight to the GitHub REST API rather than shelling out to `gh`: no child
// process, no PATH lookup, and no dependency on a CLI being installed. These
// repos are public, so a token is optional -- but unauthenticated calls are
// capped at 60/hour and each repo costs two, so set GITHUB_TOKEN for a wave of
// any size (`GITHUB_TOKEN=$(gh auth token) node create.mjs ...`).
const ghHeaders = {
  accept: 'application/vnd.github+json',
  'x-github-api-version': '2022-11-28',
  ...(process.env.GITHUB_TOKEN ? { authorization: 'Bearer ' + process.env.GITHUB_TOKEN } : {}),
};
const ghOk = async (path) => {
  const res = await fetch('https://api.github.com/' + path, { headers: ghHeaders });
  if (res.status === 403 || res.status === 429) {
    die('GitHub API rate-limited (' + res.status + '). Set GITHUB_TOKEN and re-run; nothing was created.');
  }
  return res.ok;
};

console.log('Pre-flight (GitHub' + (process.env.GITHUB_TOKEN ? ', authenticated' : ', anonymous') + '):');
for (const r of repos) {
  const slug = POLICY.repositoryOwner + '/' + r;
  if (!(await ghOk('repos/' + slug))) {
    die(slug + ' does not exist or is not visible. The repository field cannot be edited after creation, so nothing was created.');
  }
  if (!(await ghOk('repos/' + slug + '/contents/.github/workflows/' + POLICY.workflowFile))) {
    die(slug + ' has no .github/workflows/' + POLICY.workflowFile + '. A policy naming a missing workflow can never match.');
  }
  console.log('  ok  ' + slug + '  (+ ' + POLICY.workflowFile + ')');
}

const { browser, page } = await attach();
try {
  const existing = await listPolicies(page);
  const have = new Set(existing.filter((p) => p.repositoryOwner === POLICY.repositoryOwner).map((p) => p.repository));
  const todo = repos.filter((r) => !have.has(r));
  for (const r of repos) if (have.has(r)) console.log('  skip ' + r + ' -- already has a policy');
  if (!todo.length) {
    console.log('\nNothing to create.');
    await browser.close();
    process.exit(0);
  }

  console.log('\n' + (DRY ? 'DRY RUN -- filling and cancelling, nothing is submitted' : 'Creating') + ': ' + todo.length + '\n');

  for (const repo of todo) {
    console.log('--- ' + repo);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.click('#show-create-container');
    await page.waitForSelector('#__policy-name-0', { state: 'visible' });

    await page.fill('#__policy-name-0', repo);
    await page.selectOption('#__package-owner-0', { label: POLICY.owner });
    await page.selectOption('#__provider-0', POLICY.provider);
    await page.fill('#__github-repository-owner-0', POLICY.repositoryOwner);
    await page.fill('#__github-repository-0', repo);
    await page.fill('#__github-workflow-file-0', POLICY.workflowFile);
    await page.fill('#__github-environment-0', POLICY.environment);

    // Scopes: enable Push, then the "new packages and package versions" variant.
    if (!(await page.isChecked('#__push-scope-checked-0'))) await page.check('#__push-scope-checked-0');
    await page.check('#__package-push-scope-0');
    await page.fill('#__scope-subjects-input-0', POLICY.glob);

    // ---- Guard 4: read every field back and compare before submitting.
    const got = await page.evaluate(() => {
      const v = (s) => document.querySelector(s)?.value ?? null;
      const c = (s) => !!document.querySelector(s)?.checked;
      const sel = (s) => { const e = document.querySelector(s); return e ? e.options[e.selectedIndex]?.text : null; };
      return {
        name: v('#__policy-name-0'),
        ownerLabel: sel('#__package-owner-0'),
        provider: v('#__provider-0'),
        repositoryOwner: v('#__github-repository-owner-0'),
        repository: v('#__github-repository-0'),
        workflowFile: v('#__github-workflow-file-0'),
        environment: v('#__github-environment-0'),
        push: c('#__push-scope-checked-0'),
        pushNew: c('#__package-push-scope-0'),
        pushVersionsOnly: c('#__package-push-version-scope-0'),
        glob: v('#__scope-subjects-input-0'),
      };
    });
    const want = {
      name: repo,
      ownerLabel: POLICY.owner,
      provider: POLICY.provider,
      repositoryOwner: POLICY.repositoryOwner,
      repository: repo,
      workflowFile: POLICY.workflowFile,
      environment: POLICY.environment,
      push: true,
      pushNew: true,
      pushVersionsOnly: false,
      glob: POLICY.glob,
    };
    const bad = Object.keys(want).filter((k) => got[k] !== want[k]);
    if (bad.length) {
      console.error('  form read-back MISMATCH:');
      for (const k of bad) console.error('    ' + k + ': want ' + JSON.stringify(want[k]) + '  got ' + JSON.stringify(got[k]));
      die(repo + ' form did not match intent. Nothing submitted for it; policies created earlier in this run are listed above.');
    }
    console.log('  form verified: ' + JSON.stringify(got));

    if (DRY) {
      await page.click('#__cancel-edit-0');
      console.log('  cancelled (dry run)');
      continue;
    }

    await page.click('input[type=submit][value=Create]');
    await page.waitForLoadState('domcontentloaded');

    // ---- Guard 5: confirm the policy now exists, with the right immutable field.
    // The list is Knockout-rendered, so it can lag the navigation -- poll rather
    // than read once, or a successful create looks like a failed one.
    let after = null;
    for (let i = 0; i < 10 && !after; i++) {
      if (i) await page.waitForTimeout(1000);
      after = (await listPolicies(page)).find(
        (p) => p.repositoryOwner === POLICY.repositoryOwner && p.repository === repo,
      );
    }
    if (!after) {
      // Line-wise rather than one regex over the whole page: the alternation
      // inside two greedy [^\n]* runs backtracks badly (S8786).
      const complaint = await page.evaluate(() => {
        const wanted = ['must be specified', 'is required', 'should be provided', 'invalid'];
        const line = document.body.innerText
          .split('\n')
          .map((l) => l.trim())
          .find((l) => wanted.some((w) => l.toLowerCase().includes(w)));
        return line || null;
      });
      die('submitted ' + repo + ' but no matching policy came back'
        + (complaint ? '. nuget.org says: "' + complaint + '"' : '. Check the page before re-running.')
        + ' Nothing was created for it.');
    }
    if (after.workflow !== POLICY.workflowFile || after.environment !== POLICY.environment) {
      console.error('  created policy reads back wrong: ' + JSON.stringify(after));
      die(repo + ' policy was created with unexpected values. Workflow/environment are editable -- fix in the UI.');
    }
    console.log('  created [' + after.policyId + '] repo=' + after.repository + ' wf=' + after.workflow + ' env=' + after.environment + ' scopes=' + after.scopes);
    if (after.needsActivation) console.log('  NOTE: shows "Activate for 7 days" -- release within the window or re-activate.');
  }
  console.log('\nDone.');
} finally {
  await browser.close();
}
