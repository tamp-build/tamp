import { attach, listPolicies } from './lib.mjs';
const { browser, page } = await attach();
const rows = await listPolicies(page);
console.log(`${rows.length} existing policies\n`);
for (const r of rows) {
  console.log(`[${r.policyId}] ${r.name}`);
  console.log(`      owner=${r.owner}  repo=${r.repositoryOwner}/${r.repository}  wf=${r.workflow}  env=${r.environment}`);
  console.log(`      scopes=${r.scopes}  glob=${r.glob}  status=${r.status}`);
}
await browser.close();
