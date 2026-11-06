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

function isLocal(url) {
  try {
    const u = new URL(url);
    if (u.protocol === 'file:' || u.protocol === 'data:' || u.protocol === 'blob:' || u.protocol === 'devtools:') return true;
    return LOCAL_HOSTS.has(u.hostname);
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
