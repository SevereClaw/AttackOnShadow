/*
 * AttackOnShadow — extra features in the renderer:
 *   - auto profiles: switch the keyboard's onboard layer when a game is running
 *   - menu sounds: plays the user's own .wav/.mp3/.ogg files on tab/button clicks
 *   - window scale controls (the resizing itself happens in aos-main.js)
 * plus the mod panel that configures all of it (button in the header).
 *
 * Settings are kept in %APPDATA%\Attack Shark Driver\aos-settings.json so they
 * survive reinstalling the mod and work when the driver lives in Program Files.
 */
(function () {
  'use strict';
  if (!window.require) return; // plain browser: nothing to do
  var fs = window.require('fs');
  var path = window.require('path');
  var url = window.require('url');
  var remote = window.require('@electron/remote');
  var cp = window.require('child_process');
  var ipc = window.require('electron').ipcRenderer;
  var isWin = (window.process && window.process.platform) === 'win32';

  var USER_DIR = remote.app.getPath('userData');
  var SETTINGS = path.join(USER_DIR, 'aos-settings.json');
  var SOUND_DIRS = [path.join(USER_DIR, 'aos-sounds'), path.join(remote.app.getAppPath(), 'aos', 'sounds')];

  // ---------- settings ----------
  var settings = { auto: { enabled: true, rules: [], fallback: null, interval: 3 }, sounds: { enabled: true, volume: 60 } };
  try {
    var saved = JSON.parse(fs.readFileSync(SETTINGS, 'utf8'));
    if (saved.auto) Object.assign(settings.auto, saved.auto);
    if (saved.sounds) Object.assign(settings.sounds, saved.sounds);
  } catch (e) { /* first run */ }
  function save() {
    try { fs.mkdirSync(USER_DIR, { recursive: true }); fs.writeFileSync(SETTINGS, JSON.stringify(settings, null, 2)); }
    catch (e) { console.warn('[AttackOnShadow] settings not saved', e); }
  }

  function stores() { return window.__aosStores; }
  function device() { var s = stores(); return s && s.deviceStore && s.deviceStore.currentDev; }
  function deviceKey() {
    var d = device();
    return d && d.deviceType ? String(d.deviceType.id) : null;
  }
  function deviceName() {
    var d = device();
    return d && d.deviceType ? (d.deviceType.displayName || d.deviceType.name) : null;
  }
  function currentProfile() { var s = stores(); return s && s.deviceStore ? s.deviceStore.currentProfile : undefined; }
  // The label shown in the stock layer dropdown ("Слой-2"), for display only.
  function currentLayerLabel() {
    var el = document.querySelector('.css-1w95m61');
    var t = el && el.textContent.trim();
    var p = currentProfile();
    return t || (typeof p === 'number' ? 'Профиль #' + (p + 1) : '?');
  }

  // ---------- sounds ----------
  var soundFiles = {}, lastPlay = {};
  function scanSounds() {
    soundFiles = {};
    SOUND_DIRS.slice().reverse().forEach(function (dir) {
      try {
        fs.readdirSync(dir).forEach(function (f) {
          var m = /^(\w+)\.(wav|mp3|ogg)$/i.exec(f);
          if (m) soundFiles[m[1].toLowerCase()] = url.pathToFileURL(path.join(dir, f)).href;
        });
      } catch (e) { /* folder missing */ }
    });
  }
  function play(name) {
    if (!settings.sounds.enabled) return;
    var url = soundFiles[name] || (name === 'tab' ? soundFiles.click : null);
    if (!url) return;
    var now = Date.now();
    if (lastPlay[name] && now - lastPlay[name] < 60) return;
    lastPlay[name] = now;
    try { var a = new Audio(url); a.volume = Math.max(0, Math.min(1, settings.sounds.volume / 100)); a.play().catch(function () {}); } catch (e) { /* ignore */ }
  }
  scanSounds();

  document.addEventListener('click', function (ev) {
    var t = ev.target;
    if (!(t instanceof Element)) return;
    if (t.closest('.aos-tab')) { play('tab'); return; }
    var el = t.closest('#root *, .aos-modal *');
    if (el && getComputedStyle(el).cursor === 'pointer') play('click');
  }, true);
  var lastHover = null;
  document.addEventListener('mouseover', function (ev) {
    var t = ev.target instanceof Element ? ev.target.closest('.aos-tab') : null;
    if (t && t !== lastHover) play('hover');
    lastHover = t;
  }, true);
  ipc.on('show', function () { play('open'); });
  setTimeout(function () { play('open'); }, 1500);

  // ---------- auto profiles ----------
  var state = { running: [], matched: null, lastTarget: undefined, lastSwitch: '' };

  function listProcesses(cb) {
    if (!isWin) { cb((window.__aosFakeProcesses || []).slice()); return; }
    cp.exec('tasklist /fo csv /nh', { windowsHide: true, maxBuffer: 4 * 1024 * 1024 }, function (err, out) {
      if (err || !out) return cb([]);
      var names = {};
      String(out).split(/\r?\n/).forEach(function (line) {
        var m = /^"([^"]+)"/.exec(line);
        if (m) names[m[1].toLowerCase()] = true;
      });
      cb(Object.keys(names));
    });
  }

  function desiredTarget(running) {
    var key = deviceKey();
    if (!key) return { profile: undefined };
    var set = {}; running.forEach(function (n) { set[n] = true; });
    var rules = settings.auto.rules;
    for (var i = 0; i < rules.length; i++) {
      var r = rules[i];
      if (r.device === key && set[r.exe.toLowerCase()]) return { profile: r.profile, label: r.label, rule: r };
    }
    var fb = settings.auto.fallback;
    if (fb && fb.device === key) return { profile: fb.profile, label: fb.label, rule: null };
    return { profile: undefined };
  }

  var switching = false;
  function tick() {
    listProcesses(function (running) {
      state.running = running;
      if (!settings.auto.enabled || switching) { renderStatus(); return; }
      var t = desiredTarget(running);
      state.matched = t.rule;
      var id = t.profile === undefined ? 'none' : deviceKey() + ':' + t.profile;
      // Only act when the wanted profile changes, so a layer picked by hand
      // in the UI or on the keyboard is not overridden every few seconds.
      if (id !== state.lastTarget) {
        var first = state.lastTarget === undefined;
        state.lastTarget = id;
        // On start-up (or after editing rules) don't yank the keyboard back to
        // the normal layer; only a running game forces a switch right away.
        if (first && !t.rule) { renderStatus(); return; }
        if (t.profile !== undefined && t.profile !== currentProfile()) switchTo(t.profile, t.label, t.rule);
      }
      renderStatus();
    });
  }
  function switchTo(profile, label, rule) {
    var s = stores();
    if (!s || !s.deviceStore || typeof s.deviceStore.setCurrentProfile !== 'function') return;
    switching = true;
    Promise.resolve(s.deviceStore.setCurrentProfile(profile)).then(function () {
      state.lastSwitch = label + (rule ? ' — ' + rule.exe : ' — обычный');
      play('switch');
      toast((rule ? rule.exe + ': ' : '') + label);
    }).catch(function (e) { console.warn('[AttackOnShadow] switch failed', e); })
      .then(function () { switching = false; renderStatus(); });
  }
  var timer = null;
  function restartTimer() {
    if (timer) clearInterval(timer);
    timer = setInterval(tick, Math.max(1, settings.auto.interval || 3) * 1000);
    setTimeout(tick, 500);
  }
  restartTimer();

  // ---------- small toast ----------
  var toastEl = null, toastTimer = null;
  function toast(text) {
    if (!toastEl) { toastEl = document.createElement('div'); toastEl.className = 'aos-toast'; document.body.appendChild(toastEl); }
    toastEl.textContent = text;
    toastEl.classList.add('on');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toastEl.classList.remove('on'); }, 2200);
  }

  // ---------- window scale (IPC to aos-main.js) ----------
  var scale = { percent: 100 };
  ipc.on('aos-scale', function (_, s) { scale = s; renderScale(); });
  function sendScale(action, value) { ipc.send('aos-scale', { action: action, value: value }); }
  window.addEventListener('wheel', function (ev) {
    if (!ev.ctrlKey) return;
    ev.preventDefault();
    sendScale('step', ev.deltaY < 0 ? 1 : -1);
  }, { passive: false, capture: true });
  window.addEventListener('keydown', function (ev) {
    if (!ev.ctrlKey) return;
    if (ev.key === '=' || ev.key === '+') { sendScale('step', 1); ev.preventDefault(); }
    else if (ev.key === '-') { sendScale('step', -1); ev.preventDefault(); }
    else if (ev.key === '0') { sendScale('set', 'stock'); ev.preventDefault(); }
  }, true);
  sendScale('get');

  // ---------- mod panel ----------
  function h(tag, attrs, children) {
    var el = document.createElement(tag);
    if (attrs) Object.keys(attrs).forEach(function (k) {
      if (k === 'text') el.textContent = attrs[k];
      else if (k.slice(0, 2) === 'on') el.addEventListener(k.slice(2), attrs[k]);
      else el.setAttribute(k, attrs[k]);
    });
    (children || []).forEach(function (c) { if (c) el.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
    return el;
  }

  var modal = null, statusBox = null, rulesBox = null, scaleBox = null, procSelect = null, exeInput = null;

  function openPanel() {
    if (!modal) buildPanel();
    modal.classList.add('on');
    scanSounds();
    renderAll();
    tick();
  }
  function closePanel() { if (modal) modal.classList.remove('on'); }

  function buildPanel() {
    modal = h('div', { class: 'aos-modal', onclick: function (e) { if (e.target === modal) closePanel(); } });
    var panel = h('div', { class: 'aos-sheet' });
    panel.appendChild(h('div', { class: 'aos-sheet-head' }, [
      h('span', { class: 'aos-sheet-title', text: 'AttackOnShadow' }),
      h('span', { class: 'aos-x', text: '×', onclick: closePanel }),
    ]));

    // auto profiles
    var auto = h('section', { class: 'aos-card aos-card-wide' });
    auto.appendChild(h('h3', { text: 'Автопрофили' }));
    auto.appendChild(h('p', { class: 'aos-hint', text: 'Выбери слой на главной странице, потом привяжи его к игре. Пока игра запущена, клавиатура остаётся на этом слое; когда закроешь — вернётся обычный. Работает, пока драйвер открыт или свёрнут в трей.' }));
    var enabled = h('label', { class: 'aos-check' }, [h('input', { type: 'checkbox' }), ' Автопереключение включено']);
    enabled.firstChild.checked = !!settings.auto.enabled;
    enabled.firstChild.addEventListener('change', function () { settings.auto.enabled = this.checked; state.lastTarget = undefined; save(); tick(); });
    auto.appendChild(enabled);
    statusBox = h('div', { class: 'aos-status' });
    auto.appendChild(statusBox);

    exeInput = h('input', { class: 'aos-input', placeholder: 'game.exe', spellcheck: 'false' });
    procSelect = h('select', { class: 'aos-input aos-select' });
    procSelect.addEventListener('change', function () { if (this.value) exeInput.value = this.value; });
    auto.appendChild(h('div', { class: 'aos-row' }, [
      exeInput,
      procSelect,
      h('button', { class: 'aos-btn aos-btn-main', text: 'Привязать текущий слой', onclick: addRule }),
    ]));
    rulesBox = h('div', { class: 'aos-rules' });
    auto.appendChild(rulesBox);
    auto.appendChild(h('div', { class: 'aos-row' }, [
      h('button', { class: 'aos-btn', text: 'Текущий слой — обычный', onclick: setFallback }),
      h('button', { class: 'aos-btn', text: 'Без обычного', onclick: function () { settings.auto.fallback = null; save(); renderAll(); } }),
    ]));
    panel.appendChild(auto);

    // sounds
    var snd = h('section', { class: 'aos-card' });
    snd.appendChild(h('h3', { text: 'Звуки меню' }));
    var sndOn = h('label', { class: 'aos-check' }, [h('input', { type: 'checkbox' }), ' Включены']);
    sndOn.firstChild.checked = !!settings.sounds.enabled;
    sndOn.firstChild.addEventListener('change', function () { settings.sounds.enabled = this.checked; save(); });
    snd.appendChild(sndOn);
    var vol = h('input', { type: 'range', min: '0', max: '100', class: 'aos-range' });
    vol.value = settings.sounds.volume;
    var volText = h('span', { class: 'aos-mono', text: settings.sounds.volume + '%' });
    vol.addEventListener('input', function () { settings.sounds.volume = +this.value; volText.textContent = this.value + '%'; });
    vol.addEventListener('change', function () { save(); play('click'); });
    snd.appendChild(h('div', { class: 'aos-row' }, [h('span', { text: 'Громкость' }), vol, volText]));
    snd.appendChild(h('p', { class: 'aos-hint', text: 'Файлы в папке: tab (вкладки), click (кнопки), hover (наведение), open (запуск), switch (автосмена слоя) — .wav, .mp3 или .ogg.' }));
    snd.appendChild(h('div', { class: 'aos-sounds-found' }));
    snd.appendChild(h('div', { class: 'aos-row' }, [
      h('button', { class: 'aos-btn', text: 'Открыть папку', onclick: function () {
        try { fs.mkdirSync(SOUND_DIRS[0], { recursive: true }); } catch (e) { /* exists */ }
        remote.shell.openPath(SOUND_DIRS[0]);
      } }),
      h('button', { class: 'aos-btn', text: 'Обновить', onclick: function () { scanSounds(); renderAll(); play('tab'); } }),
    ]));
    panel.appendChild(snd);

    // scale
    var sc = h('section', { class: 'aos-card' });
    sc.appendChild(h('h3', { text: 'Размер окна' }));
    scaleBox = h('div', { class: 'aos-scale-value aos-mono' });
    sc.appendChild(h('div', { class: 'aos-row' }, [
      h('button', { class: 'aos-btn aos-btn-sq', text: '−', onclick: function () { sendScale('step', -1); } }),
      scaleBox,
      h('button', { class: 'aos-btn aos-btn-sq', text: '+', onclick: function () { sendScale('step', 1); } }),
    ]));
    sc.appendChild(h('div', { class: 'aos-row' }, [
      h('button', { class: 'aos-btn', text: 'По экрану', onclick: function () { sendScale('set', 'fit'); } }),
      h('button', { class: 'aos-btn', text: 'Стандарт', onclick: function () { sendScale('set', 'stock'); } }),
    ]));
    sc.appendChild(h('p', { class: 'aos-hint', text: 'Ещё: Ctrl + колесо мыши, Ctrl + / Ctrl − / Ctrl 0. Размер запоминается.' }));
    panel.appendChild(sc);

    modal.appendChild(panel);
    document.body.appendChild(modal);
  }

  function addRule() {
    var exe = (exeInput.value || '').trim();
    if (!exe) { toast('Укажи .exe игры'); return; }
    if (!/\.exe$/i.test(exe)) exe += '.exe';
    var key = deviceKey(), p = currentProfile();
    if (!key || typeof p !== 'number') { toast('Устройство не подключено'); return; }
    settings.auto.rules = settings.auto.rules.filter(function (r) { return !(r.device === key && r.exe.toLowerCase() === exe.toLowerCase()); });
    settings.auto.rules.push({ exe: exe, device: key, deviceName: deviceName(), profile: p, label: currentLayerLabel() });
    if (!settings.auto.fallback) settings.auto.fallback = null;
    exeInput.value = '';
    state.lastTarget = undefined;
    save(); renderAll(); toast(exe + ' → ' + currentLayerLabel());
  }
  function setFallback() {
    var key = deviceKey(), p = currentProfile();
    if (!key || typeof p !== 'number') { toast('Устройство не подключено'); return; }
    settings.auto.fallback = { device: key, deviceName: deviceName(), profile: p, label: currentLayerLabel() };
    state.lastTarget = undefined;
    save(); renderAll();
  }

  function renderStatus() {
    if (!statusBox || !modal || !modal.classList.contains('on')) return;
    var dn = deviceName();
    var lines = [];
    lines.push('Устройство: ' + (dn || 'не подключено') + (dn ? ' · сейчас ' + currentLayerLabel() : ''));
    if (state.matched) lines.push('Запущена игра: ' + state.matched.exe);
    if (state.lastSwitch) lines.push('Последняя смена: ' + state.lastSwitch);
    var fb = settings.auto.fallback;
    lines.push('Обычный слой: ' + (fb ? fb.label + (fb.deviceName ? ' (' + fb.deviceName + ')' : '') : 'не задан — после игры слой останется'));
    statusBox.textContent = '';
    lines.forEach(function (l) { statusBox.appendChild(h('div', { text: l })); });
    if (state.shownMatch !== state.matched) { state.shownMatch = state.matched; renderRules(); }

    // running processes for the picker (skip obvious system ones)
    if (procSelect && document.activeElement !== procSelect) {
      var skip = /^(svchost|system|registry|smss|csrss|wininit|winlogon|services|lsass|fontdrvhost|dwm|conhost|runtimebroker|sihost|taskhostw|ctfmon|explorer|searchhost|startmenuexperiencehost|textinputhost|dllhost|wmiprvse|audiodg|spoolsv|securityhealth\w*|msmpeng|nissrv|smartscreen|tasklist|cmd|powershell|attack shark driver|iot_driver\w*)\.exe$/i;
      var list = state.running.filter(function (n) { return /\.exe$/.test(n) && !skip.test(n); }).sort();
      var cur = procSelect.value;
      procSelect.textContent = '';
      procSelect.appendChild(h('option', { value: '', text: 'Запущенные…' }));
      list.forEach(function (n) { procSelect.appendChild(h('option', { value: n, text: n })); });
      procSelect.value = list.indexOf(cur) !== -1 ? cur : '';
    }
  }
  function renderRules() {
    if (!rulesBox) return;
    rulesBox.textContent = '';
    if (!settings.auto.rules.length) { rulesBox.appendChild(h('div', { class: 'aos-empty', text: 'Правил пока нет' })); return; }
    settings.auto.rules.forEach(function (r, i) {
      rulesBox.appendChild(h('div', { class: 'aos-rule' + (state.matched === r ? ' live' : '') }, [
        h('span', { class: 'aos-rule-exe', text: r.exe }),
        h('span', { class: 'aos-rule-arrow', text: '→' }),
        h('span', { class: 'aos-rule-layer', text: r.label + (r.deviceName ? ' · ' + r.deviceName : '') }),
        h('span', { class: 'aos-rule-del', text: '×', title: 'Удалить', onclick: function () {
          settings.auto.rules.splice(i, 1); state.lastTarget = undefined; save(); renderAll();
        } }),
      ]));
    });
  }
  function renderSounds() {
    var box = modal && modal.querySelector('.aos-sounds-found');
    if (!box) return;
    var names = Object.keys(soundFiles);
    box.textContent = names.length ? 'Найдены: ' + names.sort().join(', ') : 'Звуков пока нет';
  }
  function renderScale() {
    if (scaleBox) scaleBox.textContent = Math.round(scale.percent || 100) + '%';
  }
  function renderAll() { renderStatus(); renderRules(); renderSounds(); renderScale(); }

  // entry button in the header (outside #root so React never touches it)
  function addButton() {
    if (document.querySelector('.aos-open')) return;
    var b = h('div', { class: 'aos-open', title: 'Настройки AttackOnShadow', onclick: openPanel }, [h('span', { text: 'МОД' })]);
    document.body.appendChild(b);
  }
  if (document.body) addButton(); else document.addEventListener('DOMContentLoaded', addButton);
  window.addEventListener('keydown', function (e) { if (e.key === 'Escape') closePanel(); });

  window.__aos = { openPanel: openPanel, settings: settings, tick: tick, play: play };
})();
