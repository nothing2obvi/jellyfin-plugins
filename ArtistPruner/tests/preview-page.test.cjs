const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync(require('node:path').join(__dirname, '../Jellyfin.Plugin.ArtistPruner/Configuration/configPage.html'), 'utf8');
class Element {
  constructor() { this.children = []; this.handlers = {}; this.style = {}; this.textContent = ''; }
  addEventListener(name, callback) { this.handlers[name] = callback; }
  appendChild(child) { this.children.push(child); }
  replaceChildren() { this.children = []; }
  querySelector(selector) { return nodes[selector] ||= new Element(); }
}
const nodes = {};
const page = new Element();
let response;
let fail = false;
vm.runInNewContext(html.match(/<script>([\s\S]*?)<\/script>/)[1], {
  document: { querySelector: () => page, createElement: () => new Element() },
  ApiClient: { getUrl: x => x, ajax: async () => { if (fail) throw Error('offline'); return response; } },
  Dashboard: {}, window: {}
});
(async () => {
  const button = nodes['#ShowPrunes'];
  async function click() { await button.handlers.click.call(button); }
  response = null;
  await click();
  assert.match(nodes['#PreviewStatus'].textContent, /No report yet/);
  response = {status: 'Failed', dryRun: true, startedUtc: '2026-10-13T00:00:00Z', message: 'scan failed', rows: [
    {itemId: '123', name: '<script>evil</script>', kind: 'Track', field: 'Artists', before: ['A','B'], fileCredits: ['A'], removed: ['B'], status: 'Proposed'}
  ]};
  await click();
  assert.match(nodes['#PreviewStatus'].textContent, /Failed.*scan failed/);
  assert.equal(nodes.tbody.children[0].children[0].textContent, '<script>evil</script> (123)');
  assert.equal(nodes.tbody.children[0].children[4].textContent, 'B');
  assert.equal(nodes['#PruneTable'].hidden, false);
  response = {Status: 'Completed', DryRun: true, StartedUtc: '2026-10-13T00:00:00Z', Message: 'zero eligible', Rows: []};
  await click();
  assert.equal(nodes.tbody.children.length, 0);
  assert.equal(nodes['#PruneTable'].hidden, true);
  fail = true;
  await click();
  assert.match(nodes['#PreviewStatus'].textContent, /Unable to load/);
  assert.equal(button.disabled, false);
  console.log('PASS preview page: empty, partial failure, safe text rendering, camel/Pascal casing, refresh, request error');
})().catch(error => { console.error(error); process.exitCode = 1; });
