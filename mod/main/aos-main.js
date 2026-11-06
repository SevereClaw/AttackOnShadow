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
