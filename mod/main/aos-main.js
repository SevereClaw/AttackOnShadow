// AttackOnShadow — main-process addon, loaded first thing from main_dist/main.js.
// Offline mode: every network request that does not go to this computer is
// cancelled. The UI only needs 127.0.0.1:3814 (iot_driver.exe, which talks to
// the keyboard/mouse over USB); everything else is the vendor cloud
// (accounts, sharing, update checks, iot_driver self-download) and analytics.
'use strict';
const fs = require('fs');
const path = require('path');
const { app, session, nativeImage } = require('electron');

let config = { offline: true };
try {
  config = Object.assign(config, JSON.parse(fs.readFileSync(path.join(__dirname, 'config.json'), 'utf8')));
} catch (e) { /* defaults */ }

const LOCAL_HOSTS = new Set(['127.0.0.1', 'localhost', '::1', '[::1]']);
// The only internet the mod itself uses: checking/downloading its own releases.
const UPDATE_HOSTS = new Set(['api.github.com', 'github.com', 'codeload.github.com', 'objects.githubusercontent.com']);

function isLocal(url) {
  try {
    const u = new URL(url);
    if (u.protocol === 'file:' || u.protocol === 'data:' || u.protocol === 'blob:' || u.protocol === 'devtools:') return true;
    return LOCAL_HOSTS.has(u.hostname) || UPDATE_HOSTS.has(u.hostname);
  } catch (e) {
    return false;
  }
}

if (config.offline) {
  app.on('ready', () => {
    const blocked = new Set();
    session.defaultSession.webRequest.onBeforeRequest((details, callback) => {
      if (isLocal(details.url)) return callback({});
      const host = (() => { try { return new URL(details.url).host; } catch (e) { return details.url; } })();
      if (!blocked.has(host)) { blocked.add(host); console.log('[AttackOnShadow] offline: blocked', host); }
      callback({ cancel: true });
    });
  });
}

// Window / taskbar icon: the butterfly instead of the shark. (The .exe file itself
// keeps its embedded icon; that only shows in Explorer and shortcuts.)
app.on('browser-window-created', (_, win) => {
  try {
    const icon = nativeImage.createFromPath(path.join(__dirname, 'aos-icon.png'));
    if (!icon.isEmpty()) win.setIcon(icon);
  } catch (e) { /* keep stock icon */ }
});

// ---------- window scale ----------
// The UI is laid out at 1200x788 and the renderer zooms it to the window height
// (zoom = height / 788). Stock behaviour only ever shrinks the window; here the
// size is a user setting: 'stock', 'fit' (80% of screen height) or a number.
const { ipcMain, screen, BrowserWindow } = require('electron');
const BASE_W = 1200, BASE_H = 788;
const STEPS = [0.6, 0.7, 0.8, 0.9, 1, 1.1, 1.25, 1.4, 1.5, 1.65, 1.8, 2, 2.25, 2.5];
const WINDOW_FILE = () => path.join(app.getPath('userData'), 'aos-window.json');

function loadWindowPrefs() {
  try { return JSON.parse(fs.readFileSync(WINDOW_FILE(), 'utf8')); } catch (e) { return { scale: 'stock' }; }
}
function saveWindowPrefs(p) {
  try { fs.mkdirSync(app.getPath('userData'), { recursive: true }); fs.writeFileSync(WINDOW_FILE(), JSON.stringify(p, null, 2)); } catch (e) { /* ignore */ }
}
function maxRatio(display) {
  const wa = (display || screen.getPrimaryDisplay()).workArea;
  return Math.min(wa.width / BASE_W, wa.height / BASE_H);
}
function resolveRatio(pref, stockR, display) {
  const max = maxRatio(display);
  if (pref === 'fit') return Math.min(stockR, max);     // stockR = 80% of screen height / 788, uncapped
  if (typeof pref === 'number' && pref > 0) return Math.max(0.5, Math.min(pref, max));
  return stockR > 1 ? 1 : stockR;                       // stock: never bigger than 1200x788
}

// main.js calls this instead of its own `ratio = r > 1 ? 1 : r`
global.__aosRatio = function (r) {
  return resolveRatio(loadWindowPrefs().scale, r);
};

function applyScale(win, ratio) {
  const w = Math.round(BASE_W * ratio), h = Math.round(BASE_H * ratio);
  const display = screen.getDisplayMatching(win.getBounds());
  const wa = display.workArea;
  const b = win.getBounds();
  // keep the window centred on where it was, but inside the screen
  let x = Math.round(b.x + (b.width - w) / 2), y = Math.round(b.y + (b.height - h) / 2);
  x = Math.max(wa.x, Math.min(x, wa.x + wa.width - w));
  y = Math.max(wa.y, Math.min(y, wa.y + wa.height - h));
  const wasResizable = win.isResizable();
  if (!wasResizable) win.setResizable(true);
  win.setBounds({ x, y, width: w, height: h });
  if (!wasResizable) win.setResizable(false);
  win.webContents.setZoomFactor(h / BASE_H);
}

function currentRatio(win) {
  return win.getContentBounds().height / BASE_H;
}

ipcMain.on('aos-scale', (event, msg) => {
  const win = BrowserWindow.fromWebContents(event.sender);
  if (!win || !msg) return;
  const prefs = loadWindowPrefs();
  const display = screen.getDisplayMatching(win.getBounds());
  const stockR = Math.round(display.size.height * 0.8) / BASE_H;
  if (msg.action === 'step') {
    const cur = currentRatio(win), max = maxRatio(display);
    const list = STEPS.filter((s) => s <= max + 1e-6);
    let next = cur;
    if (msg.value > 0) next = list.find((s) => s > cur + 0.01) || list[list.length - 1] || cur;
    else next = list.slice().reverse().find((s) => s < cur - 0.01) || list[0] || cur;
    prefs.scale = next;
    applyScale(win, resolveRatio(next, stockR, display));
  } else if (msg.action === 'set') {
    prefs.scale = msg.value;
    applyScale(win, resolveRatio(msg.value, stockR, display));
  }
  if (msg.action !== 'get') saveWindowPrefs(prefs);
  event.sender.send('aos-scale', { percent: Math.round(currentRatio(win) * 100), pref: prefs.scale });
});

// ---------- mod updates (GitHub releases) ----------
const { net, shell } = require('electron');
const os = require('os');
const childProcess = require('child_process');

function modInfo() {
  try { return JSON.parse(fs.readFileSync(path.join(__dirname, 'version.json'), 'utf8')); }
  catch (e) { return { version: '0.0.0', repo: 'SevereClaw/AttackOnShadow' }; }
}
function cmpVersion(a, b) {
  const pa = String(a).replace(/^v/i, '').split(/[.-]/).map((x) => parseInt(x, 10) || 0);
  const pb = String(b).replace(/^v/i, '').split(/[.-]/).map((x) => parseInt(x, 10) || 0);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    if ((pa[i] || 0) !== (pb[i] || 0)) return (pa[i] || 0) > (pb[i] || 0) ? 1 : -1;
  }
  return 0;
}
function httpGet(url, asBuffer) {
  return new Promise((resolve, reject) => {
    const req = net.request({ url, redirect: 'follow' });
    req.setHeader('User-Agent', 'AttackOnShadow-updater');
    if (!asBuffer) req.setHeader('Accept', 'application/vnd.github+json');
    req.on('response', (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => {
        const buf = Buffer.concat(chunks);
        if (res.statusCode < 200 || res.statusCode >= 300) return reject(new Error('HTTP ' + res.statusCode));
        resolve(asBuffer ? buf : JSON.parse(buf.toString('utf8')));
      });
      res.on('error', reject);
    });
    req.on('error', reject);
    req.end();
  });
}
let latestRelease = null;
async function checkUpdate() {
  const info = modInfo();
  const rel = await httpGet('https://api.github.com/repos/' + info.repo + '/releases/latest');
  latestRelease = rel;
  const latest = String(rel.tag_name || '').replace(/^v/i, '');
  return {
    state: cmpVersion(latest, info.version) > 0 ? 'available' : 'latest',
    current: info.version, latest, name: rel.name || rel.tag_name, notes: rel.body || '', url: rel.html_url,
  };
}
function findFile(dir, rel, depth) {
  if (fs.existsSync(path.join(dir, rel))) return path.join(dir, rel);
  if (depth <= 0) return null;
  for (const n of fs.readdirSync(dir)) {
    const p = path.join(dir, n);
    if (fs.statSync(p).isDirectory()) { const f = findFile(p, rel, depth - 1); if (f) return f; }
  }
  return null;
}
async function installUpdate(send) {
  if (process.platform !== 'win32') throw new Error('Обновление из панели работает только на Windows');
  if (!latestRelease) await checkUpdate();
  const tag = String(latestRelease.tag_name || 'latest').replace(/[^\w.-]/g, '');
  const work = path.join(os.tmpdir(), 'aos-update', tag);
  fs.rmSync(work, { recursive: true, force: true });
  fs.mkdirSync(work, { recursive: true });
  send({ state: 'downloading' });
  const zip = path.join(work, 'mod.zip');
  fs.writeFileSync(zip, await httpGet(latestRelease.zipball_url, true));
  send({ state: 'extracting' });
  const q = (p) => "'" + p.replace(/'/g, "''") + "'";
  childProcess.execFileSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
    'Expand-Archive -LiteralPath ' + q(zip) + ' -DestinationPath ' + q(path.join(work, 'x')) + ' -Force'], { windowsHide: true });
  const runCmd = findFile(path.join(work, 'x'), path.join('patch', 'run.cmd'), 2);
  if (!runCmd) throw new Error('В архиве нет patch\\run.cmd');
  send({ state: 'installing' });
  // run.cmd closes the driver, patches it (asking for admin if needed) and starts it again
  const exe = app.getPath('exe');
  const env = Object.assign({}, process.env); delete env.ELECTRON_RUN_AS_NODE;
  childProcess.spawn('cmd.exe', ['/c', 'start "AttackOnShadow update" "' + runCmd + '" install "' + exe + '" --relaunch'],
    { detached: true, stdio: 'ignore', windowsVerbatimArguments: true, env }).unref();
}

ipcMain.on('aos-update', async (event, msg) => {
  const send = (v) => { try { event.sender.send('aos-update', v); } catch (e) { /* window gone */ } };
  try {
    if (!msg || msg.action === 'info') send({ state: 'idle', current: modInfo().version });
    else if (msg.action === 'check') { send({ state: 'checking' }); send(await checkUpdate()); }
    else if (msg.action === 'install') await installUpdate(send);
    else if (msg.action === 'notes' && latestRelease) shell.openExternal(latestRelease.html_url);
  } catch (e) {
    send({ state: 'error', error: String(e && e.message || e), current: modInfo().version });
  }
});
