// Writes the API reference in docs/API.md from web/js/api-reference.js, the same source the dashboard's API
// window uses, so the two never drift apart. CI runs it with --check and fails when docs/API.md is out of date.
//   node tools/api-docs.mjs           update docs/API.md
//   node tools/api-docs.mjs --check   exit 1 if it isn't up to date
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import {
  GROUPS, ENDPOINTS, INTRO, STREAM_ABOUT, CATEGORIES, KINDS, ACCESS, SAMPLE, examples, streamExample, urlOf,
} from '../web/js/api-reference.js';

const file = fileURLToPath(new URL('../docs/API.md', import.meta.url));
const START = '<!-- api-reference:start -->';
const END = '<!-- api-reference:end -->';

const anchor = (s) => s.toLowerCase().replace(/[^a-z0-9 -]/g, '').trim().replace(/ /g, '-');
const fence = (lang, code) => `\`\`\`${lang}\n${code}\n\`\`\``;

function reference() {
  const out = [START, '<!-- Generated from web/js/api-reference.js by tools/api-docs.mjs. Edit that file, then run: node tools/api-docs.mjs -->', ''];
  for (const [title, paras] of INTRO) out.push(`## ${title}`, '', ...paras.flatMap((p) => [p, '']));

  out.push('## Endpoints', '', '| Method | Path | Needs | What |', '| --- | --- | --- | --- |');
  for (const ep of ENDPOINTS) out.push(`| ${ep.method} | [\`${ep.path}\`](#${anchor(ep.summary)}) | ${ACCESS[ep.access]} | ${ep.summary} |`);
  out.push(`| GET | [\`/ws\`](#live-readings-websocket) | read key | live readings, pushed every interval |`, '');

  for (const g of GROUPS) {
    out.push(`## ${g.title}`, '', g.about, '');
    for (const ep of g.endpoints) {
      out.push(`### ${ep.summary}`, '', `\`${ep.method} ${ep.path}\` · ${ACCESS[ep.access]}`, '');
      if (ep.about) out.push(ep.about, '');
      if (ep.params?.length) {
        out.push('| Parameter | |', '| --- | --- |', ...ep.params.map(([k, v]) => `| \`${k}\` | ${v} |`), '');
      }
      const ex = examples(ep, SAMPLE);
      out.push(fence('bash', ex.curl), '');
      out.push('<details><summary>PowerShell, Python, JavaScript</summary>', '',
        fence('powershell', ex.powershell), '', fence('python', ex.python), '', fence('javascript', ex.javascript), '', '</details>', '');
      if (ex.browser && ep.access !== 'none') out.push(`In a browser: \`${ex.browser}\``, '');
      if (ep.response) out.push('Answer:', '', fence('json', ep.response(SAMPLE)), '');
      else if (ep.answer) out.push(`Answer: ${ep.answer}`, '');
    }
  }

  out.push('## Live readings (WebSocket)', '', ...STREAM_ABOUT.flatMap((p) => [p, '']));
  const ws = streamExample(SAMPLE);
  out.push(fence('javascript', ws.javascript), '', fence('python', ws.python), '');

  out.push('## Categories and kinds', '', 'Every sensor has one of each (`category`, `kind` in its answer); filter with `?category=` and `?kind=`.', '');
  out.push('| category | | |', '| --- | --- | --- |', ...CATEGORIES.map(([id, name, what]) => `| \`${id}\` | ${name} | ${what} |`), '');
  out.push('| kind | | units |', '| --- | --- | --- |', ...KINDS.map(([id, name, units]) => `| \`${id}\` | ${name} | ${units} |`), '');
  out.push(`Example: everything about the GPU's temperatures: \`${urlOf(SAMPLE, { path: '/api/sensors', query: { category: 'gpu', kind: 'temperature' } })}\``, '');
  out.push(END);
  return out.join('\n');
}

// Git checks the file out with CRLF line endings on Windows: compare and write in the file's own line endings.
const raw = readFileSync(file, 'utf8');
const eol = raw.includes('\r\n') ? '\r\n' : '\n';
const current = raw.replace(/\r\n/g, '\n');
const a = current.indexOf(START);
const b = current.indexOf(END);
if (a < 0 || b < a) { console.error(`docs/API.md needs the ${START} … ${END} markers.`); process.exit(1); }
const next = current.slice(0, a) + reference() + current.slice(b + END.length);
if (process.argv.includes('--check')) {
  if (next !== current) { console.error('docs/API.md is out of date: run  node tools/api-docs.mjs  and commit it.'); process.exit(1); }
  console.log('docs/API.md is up to date.');
} else if (next !== current) {
  writeFileSync(file, next.replace(/\n/g, eol));
  console.log('docs/API.md updated.');
} else console.log('docs/API.md was already up to date.');
