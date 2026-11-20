import { chromium } from 'playwright-core';

export const CDP = process.env.TP_CDP || 'http://127.0.0.1:9222';
export const PAGE_URL = 'https://www.nuget.org/account/trustedpublishing';

export async function attach() {
  const browser = await chromium.connectOverCDP(CDP);
  const ctx = browser.contexts()[0];
  if (!ctx) throw new Error('No browser context over CDP at ' + CDP);
  let page = ctx.pages().find(p => p.url().includes('nuget.org/account/trustedpublishing'));
  if (!page) {
    page = ctx.pages().find(p => p.url().includes('nuget.org')) ?? await ctx.newPage();
    await page.goto(PAGE_URL, { waitUntil: 'domcontentloaded' });
  }
  // Signed-out pages render a sign-in prompt and no create toggle.
  if (!(await page.$('#show-create-container'))) {
    throw new Error('Trusted Publishing form not present — is the session signed in? URL: ' + page.url());
  }
  return { browser, page };
}

const field = (t, label) => {
  const m = t.match(new RegExp(label + ':\s*([^\n#]+)'));
  return m ? m[1].trim() : null;
};

/** Parse every existing policy out of the Manage panel.
 *  Anchored on each row's Edit link: the hidden edit inputs carry the editable
 *  fields verbatim, and the repository (which is NOT editable after creation)
 *  is read from the nearest ancestor whose text carries it.
 */
export async function listPolicies(page) {
  await page.click('#show-manage-container').catch(() => {});
  await page.waitForTimeout(300);
  return page.evaluate(() => {
    const out = [];
    for (const edit of document.querySelectorAll('#manage-container a[id^="__start-edit-"]')) {
      const id = edit.id.replace('__start-edit-', '');
      let box = edit;
      while (box && box !== document.body && !/Repository:/.test(box.innerText || '')) box = box.parentElement;
      const t = (box?.innerText || '').replace(/[ 	]+/g, ' ');
      const g = label => {
        // Values usually follow the label inline, but some render on the next line.
        const m = t.match(new RegExp(label + ':[ \t]*\n?[ \t]*([^\n#]+)'));
        return m ? m[1].trim() : null;
      };
      const v = sel => document.querySelector(sel)?.value ?? null;
      out.push({
        policyId: id,
        name: v(`#__policy-name-${id}`),
        workflow: v(`#__github-workflow-file-${id}`),
        environment: v(`#__github-environment-${id}`),
        owner: g('Package owner'),
        publisher: g('Publisher'),
        repositoryOwner: g('Repository Owner'),
        repository: g('Repository'),
        scopes: g('Scopes'),
        glob: g('Glob Patterns and Packages'),
        needsActivation: /Activate for 7 days/.test(t),
      });
    }
    return out;
  });
}
