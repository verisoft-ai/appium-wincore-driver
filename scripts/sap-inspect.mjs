#!/usr/bin/env node
/**
 * SAP GUI element inspector.
 *
 * Standalone visualiser for the SAP GUI Scripting subsystem (csharp/DesktopDriverServer
 * Sap/). Spawns DesktopDriverServer.exe, attaches to the running SAP GUI session over the
 * scripting COM API, pulls the component tree, and renders it as a collapsible HTML page
 * with every attribute and screen rectangle — the SAP equivalent of Appium Inspector,
 * usable before the driver-side WebDriver wiring is complete.
 *
 * Usage:
 *   node scripts/sap-inspect.mjs [--connection 0] [--session 0] [--out <file.html>]
 *                                [--server <path to DesktopDriverServer.exe>]
 *                                [--context <sap element id>] [--xml] [--no-open]
 *
 * Prerequisites (see sap/README.md in the appium-wincore-test-apps sibling repo, which
 * stands up a local ABAP backend):
 *   - SAP Logon (saplogon.exe) running
 *   - "Enable scripting" ticked in SAP GUI Options -> Accessibility & Scripting
 *   - the two "Notify when a script ..." boxes UNticked (otherwise a modal blocks the attach)
 *   - an SAP session actually logged in (otherwise you get a clear "no open connection")
 */

import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { writeFileSync, readFileSync, existsSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import os from 'node:os';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..');

function parseArgs(argv) {
    const a = { connection: 0, session: 0, open: true, xml: false, context: null, out: null, server: null, demo: null };
    for (let i = 0; i < argv.length; i++) {
        const v = argv[i];
        if (v === '--connection') {a.connection = Number(argv[++i]);}
        else if (v === '--session') {a.session = Number(argv[++i]);}
        else if (v === '--context') {a.context = argv[++i];}
        else if (v === '--out') {a.out = argv[++i];}
        else if (v === '--server') {a.server = argv[++i];}
        else if (v === '--xml') {a.xml = true;}
        else if (v === '--no-open') {a.open = false;}
        else if (v === '--demo') {a.demo = argv[++i] ?? 'se38';}
    }
    return a;
}

function resolveServer(explicit) {
    const candidates = [
        explicit,
        path.join(repoRoot, 'csharp', 'DesktopDriverServer', 'bin', 'Debug', 'net10.0-windows', 'win-x64', 'DesktopDriverServer.exe'),
        path.join(repoRoot, 'csharp', 'DesktopDriverServer', 'bin', 'Release', 'net10.0-windows', 'win-x64', 'DesktopDriverServer.exe'),
        path.join(repoRoot, 'native', 'win-x64', 'DesktopDriverServer.exe'),
    ].filter(Boolean);
    // Prefer the most recently built binary among those that exist (dev builds win over
    // the shipped native/ copy).
    const found = candidates.filter((c) => existsSync(c));
    if (!found.length) {
        throw new Error(`DesktopDriverServer.exe not found. Build it (npm run build:native) or pass --server.\nLooked in:\n  ${candidates.join('\n  ')}`);
    }
    found.sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs);
    return found[0];
}

class Server {
    constructor(exe) {
        this.proc = spawn(exe, [], { stdio: ['pipe', 'pipe', 'pipe'] });
        this.rl = createInterface({ input: this.proc.stdout });
        this.pending = new Map();
        this.nextId = 1;
        this.rl.on('line', (line) => {
            let msg;
            try { msg = JSON.parse(line); } catch { return; }
            const p = this.pending.get(msg.id);
            if (!p) {return;}
            this.pending.delete(msg.id);
            if (msg.error) {
                p.reject(new Error(`${msg.error.code}: ${msg.error.message}`));
            } else {
                p.resolve(msg.result);
            }
        });
        this.proc.stderr.on('data', (d) => process.env.SAP_INSPECT_DEBUG && process.stderr.write(`[server] ${d}`));
    }
    call(method, params = {}) {
        const id = this.nextId++;
        this.proc.stdin.write(JSON.stringify({ id, method, params }) + '\n');
        return new Promise((resolve, reject) => {
            this.pending.set(id, { resolve, reject });
            setTimeout(() => {
                if (this.pending.has(id)) {
                    this.pending.delete(id);
                    reject(new Error(`Timed out waiting for '${method}'. A SAP script-notification modal may be blocking — untick the "Notify when a script..." boxes in SAP GUI Options.`));
                }
            }, 20000);
        });
    }
    async close() {
        try { this.proc.stdin.write(JSON.stringify({ id: this.nextId++, method: 'dispose', params: {} }) + '\n'); } catch { /* */ }
        try { this.proc.kill(); } catch { /* */ }
    }
}

// ── tiny XML → node tree (no deps) ────────────────────────────────────────────
function parseXml(xml) {
    let i = 0;
    function parseNode() {
        while (i < xml.length && xml[i] !== '<') {i++;}
        if (xml.startsWith('<?', i)) { i = xml.indexOf('?>', i) + 2; return parseNode(); }
        if (xml[i] !== '<') {return null;}
        i++; // <
        let name = '';
        while (i < xml.length && !/[\s/>]/.test(xml[i])) {name += xml[i++];}
        const attrs = {};
        while (i < xml.length && xml[i] !== '>' && xml[i] !== '/') {
            while (/\s/.test(xml[i])) {i++;}
            if (xml[i] === '>' || xml[i] === '/') {break;}
            let an = '';
            while (i < xml.length && !/[\s=]/.test(xml[i])) {an += xml[i++];}
            while (/[\s=]/.test(xml[i])) {i++;}
            const q = xml[i++];
            let av = '';
            while (i < xml.length && xml[i] !== q) {av += xml[i++];}
            i++; // closing quote
            attrs[an] = decode(av);
        }
        const node = { name, attrs, children: [], text: '' };
        if (xml[i] === '/') { i += 2; return node; }
        i++; // >
        while (i < xml.length) {
            while (i < xml.length && /\s/.test(xml[i])) {i++;}
            if (xml.startsWith('</', i)) { i = xml.indexOf('>', i) + 1; break; }
            if (xml[i] === '<') { const c = parseNode(); if (c) {node.children.push(c);} }
            else { let t = ''; while (i < xml.length && xml[i] !== '<') {t += xml[i++];} node.text += decode(t.trim()); }
        }
        return node;
    }
    return parseNode();
}
function decode(s) {
    return s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');
}
function esc(s) {
    return String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function countNodes(n) {
    return 1 + n.children.reduce((s, c) => s + countNodes(c), 0);
}

function renderTree(node) {
    const id = node.attrs.Id ?? '';
    const type = node.name;
    const label = node.attrs.Name || node.attrs.Text || '';
    const kids = node.children.length
        ? `<ul>${node.children.map(renderTree).join('')}</ul>`
        : '';
    const attrRows = Object.entries(node.attrs)
        .filter(([k]) => k !== '__sapNodeKey')
        .map(([k, v]) => `<tr><th>${esc(k)}</th><td>${esc(v)}</td></tr>`).join('');
    return `<li>
      <details${node.children.length && node.children.length < 25 ? ' open' : ''}>
        <summary><span class="ty">${esc(type)}</span>${label ? `<span class="nm">${esc(label)}</span>` : ''}<code class="id">${esc(id)}</code></summary>
        <table class="attrs">${attrRows}</table>
        ${kids}
      </details>
    </li>`;
}

function buildHtml({ tree, status, xml, meta }) {
    const body = tree
        ? `<ul class="root">${renderTree(tree)}</ul>`
        : `<div class="empty">
             <h2>No SAP session to inspect</h2>
             <p>${esc(status?.message ?? 'The SAP GUI scripting engine returned no component tree.')}</p>
             <p class="hint">Log into an SAP system in SAP Logon, then run this again.</p>
           </div>`;
    return `<!doctype html><html><head><meta charset="utf-8"><title>SAP GUI Inspector</title>
<style>
  :root{color-scheme:light dark}
  body{font:13px/1.5 "IBM Plex Mono",ui-monospace,SFMono-Regular,Menlo,monospace;margin:0;background:#0f1720;color:#dce4ec}
  header{padding:14px 20px;background:#16232e;border-bottom:2px solid #b06f1a}
  header h1{margin:0;font-size:15px;color:#e2a04a;letter-spacing:.04em}
  header .meta{color:#8fa2ad;font-size:11px;margin-top:4px}
  .wrap{padding:16px 20px 80px}
  ul{list-style:none;margin:0;padding-left:16px}
  ul.root{padding-left:0}
  li{border-left:1px solid #294050;margin:1px 0}
  summary{cursor:pointer;padding:2px 6px;border-radius:4px}
  summary:hover{background:#1c2d3a}
  .ty{color:#7fd1a8;font-weight:600}
  .nm{color:#dce4ec;margin-left:8px}
  .id{color:#8fa2ad;margin-left:8px;font-size:11px}
  table.attrs{margin:4px 0 6px 18px;border-collapse:collapse;font-size:11px}
  table.attrs th{text-align:right;color:#8fa2ad;padding:1px 8px 1px 0;font-weight:400;vertical-align:top}
  table.attrs td{color:#cbd5dd;padding:1px 0;word-break:break-all}
  .empty{max-width:520px;margin:60px auto;text-align:center;color:#9fb1bd}
  .empty h2{color:#e2a04a}
  .hint{color:#7c8b96;font-size:12px}
  details>summary::-webkit-details-marker{color:#5b6b78}
</style></head><body>
<header>
  <h1>SAP GUI Inspector</h1>
  <div class="meta">${esc(meta)}</div>
</header>
<div class="wrap">${body}</div>
${xml ? `<script>console.log(${JSON.stringify(xml)})</script>` : ''}
</body></html>`;
}

async function main() {
    const args = parseArgs(process.argv.slice(2));

    // --demo: render a bundled synthetic fixture instead of attaching. Lets you see the
    // visualiser output before a live SAP session is available.
    if (args.demo) {
        const fx = path.join(repoRoot, 'test', 'fixtures', 'sap', `synthetic-${args.demo}.xml`);
        if (!existsSync(fx)) {
            throw new Error(`No demo fixture at ${fx}`);
        }
        const xml = readFileSync(fx, 'utf8');
        const tree = parseXml(xml);
        const meta = `DEMO — synthetic fixture ${path.basename(fx)}  ·  ${countNodes(tree)} nodes (not a live SAP session)`;
        const out = args.out ?? path.join(os.tmpdir(), 'sap-inspect.html');
        writeFileSync(out, buildHtml({ tree, status: { attached: true }, xml, meta }));
        console.log(`\n→ ${out}`);
        if (args.open) {openFile(out);}
        process.exit(0);
    }

    const exe = resolveServer(args.server);
    console.log(`server: ${exe}`);

    const server = new Server(exe);
    let status = null;
    let xml = null;
    let tree = null;
    try {
        await server.call('debug:ping');
        console.log(`attaching (connection=${args.connection}, session=${args.session})…`);
        status = await server.call('sap.attach', { connectionIndex: args.connection, sessionIndex: args.session });
        console.log(`attach: ${JSON.stringify(status)}`);

        if (status?.attached) {
            xml = await server.call('sap.pageSource', args.context ? { contextElementId: args.context } : {});
            tree = parseXml(xml);
            console.log(`tree: ${countNodes(tree)} nodes`);
        }
    } catch (err) {
        console.error(`\n✖ ${err.message}\n`);
        status = status ?? { attached: false, message: err.message };
    } finally {
        await server.close();
    }

    if (args.xml && xml) {
        const outXml = args.out ? args.out.replace(/\.html?$/, '.xml') : path.join(os.tmpdir(), 'sap-pagesource.xml');
        writeFileSync(outXml, xml);
        console.log(`xml → ${outXml}`);
    }

    const meta = status?.attached
        ? `system=${esc(status.sessionInfo?.systemName ?? status.system ?? '?')}  client=${esc(status.sessionInfo?.client ?? '?')}  user=${esc(status.sessionInfo?.user ?? '?')}  txn=${esc(status.sessionInfo?.transaction ?? '?')}  ·  ${tree ? countNodes(tree) : 0} nodes`
        : `not attached — ${esc(status?.reason ?? 'error')}`;

    const out = args.out ?? path.join(os.tmpdir(), 'sap-inspect.html');
    writeFileSync(out, buildHtml({ tree, status, xml, meta }));
    console.log(`\n→ ${out}`);

    if (args.open) {openFile(out);}

    process.exit(status?.attached ? 0 : 2);
}

function openFile(file) {
    const opener = process.platform === 'win32' ? 'cmd' : (process.platform === 'darwin' ? 'open' : 'xdg-open');
    const openArgs = process.platform === 'win32' ? ['/c', 'start', '', file] : [file];
    const p = spawn(opener, openArgs, { detached: true, stdio: 'ignore' });
    p.unref();
}

main().catch((e) => { console.error(e); process.exit(1); });
