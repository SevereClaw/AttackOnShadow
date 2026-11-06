// AttackOnShadow patcher.
//
// Runs inside the driver's own Electron in Node mode (see install.bat), so no
// Node.js install is needed:   ELECTRON_RUN_AS_NODE=1 "Attack Shark Driver.exe" patch.js install
// It can also run under a normal Node.js (16+):   node patch.js install "C:\path\to\Attack Shark Driver"
//
// Commands: install | uninstall | status
// Everything it changes is backed up to resources/app/.aos-backup first;
// uninstall puts those files back and removes the mod folders.
'use strict';
const fs = require('fs');
const path = require('path');

const MOD_ROOT = path.resolve(__dirname, '..', 'mod');
const TESTED_VERSION = '2.1.79';
const MARK = '/*AttackOnShadow*/';

// ---------- helpers ----------
const log = (...a) => console.log(...a);
const warn = (...a) => console.log('  ! ' + a.join(' '));
function read(p) { return fs.readFileSync(p, 'utf8'); }
function write(p, s) { fs.writeFileSync(p, s, 'utf8'); }
function mkdirp(p) { fs.mkdirSync(p, { recursive: true }); }
function rmrf(p) { if (fs.existsSync(p)) fs.rmSync ? fs.rmSync(p, { recursive: true, force: true }) : fs.rmdirSync(p, { recursive: true }); }
function copyDir(src, dst) {
  mkdirp(dst);
  for (const name of fs.readdirSync(src)) {
    const s = path.join(src, name), d = path.join(dst, name);
    if (fs.statSync(s).isDirectory()) copyDir(s, d); else fs.copyFileSync(s, d);
  }
}

function findAppDir(arg) {
  const candidates = [];
  if (arg) {
    let a = path.resolve(arg);
    if (/\.exe$/i.test(a)) a = path.dirname(a);
    candidates.push(path.join(a, 'resources', 'app'), path.join(a, 'app'), a);
  }
  if (process.versions.electron) candidates.push(path.join(path.dirname(process.execPath), 'resources', 'app'));
  for (const c of candidates) {
    if (fs.existsSync(path.join(c, 'package.json')) && fs.existsSync(path.join(c, 'main_dist', 'main.js'))) return c;
  }
  return null;
}

function bundlePath(app) {
  const dir = path.join(app, 'dist', 'static', 'js');
  const js = fs.readdirSync(dir).filter((f) => /\.js$/.test(f));
  if (js.length !== 1) throw new Error('Ожидался один файл в dist/static/js, найдено: ' + js.join(', '));
  return path.join(dir, js[0]);
}

function companyConfigs(app) {
  const dir = path.join(app, 'company');
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir).map((d) => path.join(dir, d, 'CONFIG.json')).filter((p) => fs.existsSync(p));
}

const LOGOS = ['topnav_logo.png', 'login_logo.png', 'default_logo.png', 'APP.png', 'icon.png'];

function companyLogos(app) {
  const out = [];
  for (const c of companyConfigs(app)) for (const f of LOGOS) {
    const p = path.join(path.dirname(c), f);
    if (fs.existsSync(p)) out.push(p);
  }
  return out;
}

function targets(app) {
  return [path.join(app, 'dist', 'index.html'), path.join(app, 'main_dist', 'main.js'), bundlePath(app), ...companyConfigs(app), ...companyLogos(app)];
}

// ---------- string patches for the minified renderer bundle ----------
// Each patch is anchored on an exact snippet; a missing anchor is reported and
// skipped instead of breaking the app.
function patchBundle(src) {
  const done = [];
  const rep = (name, from, to) => {
    const n = src.split(from).length - 1;
    if (n === 0) { warn('патч бандла пропущен (не найден фрагмент):', name); return; }
    src = src.split(from).join(to);
    done.push(name + (n > 1 ? ' x' + n : ''));
  };

  // 1) Google Analytics screen views -> no-op.
  rep('аналитика', 'wia.trackScreen=function(e,t,r){via.screenview(e,t,r).send()}', 'wia.trackScreen=function(e,t,r){}');

  // 2) In offline mode, hide the cloud "check for updates" and "firmware upgrade" buttons.
  rep('кнопка проверки обновлений', 'e.createElement(Tga.Button,{w:75,h:25,mode:"Common",isUpgrade:u.appNeedUpgrade,text:FE.text.检查更新()',
    'window.AOS_OFFLINE?null:e.createElement(Tga.Button,{w:75,h:25,mode:"Common",isUpgrade:u.appNeedUpgrade,text:FE.text.检查更新()');
  rep('кнопка обновления прошивки', 'e.createElement(Tga.Button,{w:75,h:25,style:{flex:"0 0 auto"},mode:"Common",isUpgrade:void 0!==f&&0!==f.upgradeMethods.length,text:FE.text.固件升级()',
    'window.AOS_OFFLINE?null:e.createElement(Tga.Button,{w:75,h:25,style:{flex:"0 0 auto"},mode:"Common",isUpgrade:void 0!==f&&0!==f.upgradeMethods.length,text:FE.text.固件升级()');

  // 3) Expose the MobX root store to the mod (auto profiles use deviceStore.setCurrentProfile).
  rep('доступ к хранилищу', ',Ida={batteryStore:new ', ',Ida=window.__aosStores={batteryStore:new ');

  // 4) Shorter Russian labels where the stock ones are cut off with "…".
  const ru = {
    '最大行程校准': 'Калибровка',
    '开始校准': 'Старт',
    '按下': 'Наж.',
    '确认': 'ОК',
    '死区设定': 'Мёртвая зона',
    '未设置功能': 'Не задано',
    '检查更新': 'Проверить',
    '高级设置': 'Расширенные',
    '模拟演示': 'Демонстрация',
    '速度': 'Скорость',
    '亮度': 'Яркость',
  };
  for (const key of Object.keys(ru)) {
    const re = new RegExp('([,{]' + key + ':\\{中文简体:"[^"]*"[^{}]*?俄语:")([^"]*)(")');
    if (re.test(src)) { src = src.replace(re, (m, a, old, c) => a + ru[key] + c); done.push('перевод «' + ru[key] + '»'); }
    else warn('перевод не найден:', key);
  }

  return { src: MARK + src, done };
}

// ---------- commands ----------
function install(app) {
  const pkg = JSON.parse(read(path.join(app, 'package.json')));
  log('Драйвер: ' + pkg.name + ' v' + pkg.version);
  if (pkg.version !== TESTED_VERSION) warn('мод проверялся на v' + TESTED_VERSION + ', у тебя v' + pkg.version + '. Ставлю, но что-то может не примениться.');

  const backup = path.join(app, '.aos-backup');
  if (fs.existsSync(backup)) {
    log('Мод уже стоял, возвращаю оригиналы перед повторной установкой…');
    restore(app, false);
  }

  // back up the originals
  const files = targets(app);
  for (const f of files) {
    if (/\.png$/i.test(f)) continue;
    if (read(f).indexOf('AttackOnShadow') !== -1) throw new Error('Файл уже пропатчен, но бэкапа нет: ' + f + '. Переустанови драйвер начисто.');
  }
  for (const f of files) {
    const dst = path.join(backup, path.relative(app, f));
    mkdirp(path.dirname(dst));
    fs.copyFileSync(f, dst);
  }
  log('Бэкап оригиналов: ' + backup);

  // mod config (kept between reinstalls)
  const aosDir = path.join(app, 'aos');
  mkdirp(aosDir);
  const cfgPath = path.join(aosDir, 'config.json');
  let cfg = { offline: true };
  if (fs.existsSync(cfgPath)) { try { cfg = Object.assign(cfg, JSON.parse(read(cfgPath))); } catch (e) { /* reset */ } }
  write(cfgPath, JSON.stringify(cfg, null, 2) + '\n');
  fs.copyFileSync(path.join(MOD_ROOT, 'main', 'aos-main.js'), path.join(aosDir, 'aos-main.js'));
  fs.copyFileSync(path.join(MOD_ROOT, 'version.json'), path.join(aosDir, 'version.json'));
  const modVersion = JSON.parse(read(path.join(MOD_ROOT, 'version.json'))).version;
  log('Мод: AttackOnShadow v' + modVersion);

  // renderer files
  const distAos = path.join(app, 'dist', 'aos');
  rmrf(distAos);
  copyDir(path.join(MOD_ROOT, 'theme'), distAos);
  write(path.join(distAos, 'aos-config.js'), 'window.AOS_OFFLINE = ' + (cfg.offline ? 'true' : 'false') + ';\n');

  // index.html: load config + theme before the app bundle
  const indexPath = path.join(app, 'dist', 'index.html');
  let html = read(indexPath);
  const inject = '<script src="./aos/aos-config.js"></script><link id="aos-theme-css" rel="stylesheet" href="./aos/aos-theme.css"><script src="./aos/aos-theme.js"></script><script src="./aos/aos-features.js"></script>';
  if (html.indexOf('</style>') === -1) throw new Error('index.html: не найден </style>');
  // (the marker goes after the doctype: anything before it would switch Chromium to quirks mode)
  html = html.replace('</style>', '</style>' + inject).replace(/<title>[^<]*<\/title>/, '<title>AttackOnShadow</title><!--' + MARK + '-->');
  write(indexPath, html);

  // main process: offline guard + window icon; tray menu labels in Russian
  // (the stock ones are Chinese until the UI loads and sends translations)
  const mainPath = path.join(app, 'main_dist', 'main.js');
  let mainSrc = read(mainPath);
  for (const [zh, ru] of [['显示', 'Показать'], ['隐藏', 'Скрыть'], ['退出', 'Выход']]) {
    const from = 'label: "' + zh + '"';
    if (mainSrc.indexOf(from) === -1) warn('пункт трея не найден:', zh);
    else mainSrc = mainSrc.split(from).join('label: "' + ru + '"');
  }
  // window size comes from the mod's scale setting (aos-main.js)
  const ratioFrom = '  ratio = r > 1 ? 1 : r;';
  if (mainSrc.indexOf(ratioFrom) === -1) warn('размер окна: фрагмент не найден, масштаб будет только через Ctrl+колесо');
  else mainSrc = mainSrc.replace(ratioFrom, '  ratio = global.__aosRatio ? global.__aosRatio(r) : (r > 1 ? 1 : r);');
  write(mainPath, MARK + 'try{require(require("path").join(__dirname,"..","aos","aos-main.js"))}catch(e){console.error("[AttackOnShadow]",e)}\n' + mainSrc);
  fs.copyFileSync(path.join(MOD_ROOT, 'assets', 'aos-icon.png'), path.join(aosDir, 'aos-icon.png'));

  // logos and tray icon: the butterfly
  for (const c of companyConfigs(app)) for (const f of LOGOS) {
    const src = path.join(MOD_ROOT, 'assets', f);
    if (fs.existsSync(src)) fs.copyFileSync(src, path.join(path.dirname(c), f));
  }

  // renderer bundle
  const bp = bundlePath(app);
  const res = patchBundle(read(bp));
  write(bp, res.src);
  log('Бандл: ' + res.done.join(', '));

  // vendor config: singleDriver hides login, sharing and account tabs
  for (const c of companyConfigs(app)) {
    const j = JSON.parse(read(c));
    j.singleDriver = true;
    write(c, JSON.stringify(j, null, 4));
  }

  write(path.join(aosDir, 'INSTALLED'), JSON.stringify({ mod: 'AttackOnShadow', version: modVersion, driver: pkg.version, date: new Date().toISOString() }) + '\n');
  log('');
  log('Готово. Офлайн-режим: ' + (cfg.offline ? 'включён' : 'выключен') + ' (aos\\config.json).');
}

function restore(app, verbose) {
  const backup = path.join(app, '.aos-backup');
  if (!fs.existsSync(backup)) { if (verbose) log('Мод не установлен (нет .aos-backup).'); return false; }
  const walk = (dir) => {
    for (const name of fs.readdirSync(dir)) {
      const p = path.join(dir, name);
      if (fs.statSync(p).isDirectory()) walk(p);
      else { const dst = path.join(app, path.relative(backup, p)); mkdirp(path.dirname(dst)); fs.copyFileSync(p, dst); }
    }
  };
  walk(backup);
  rmrf(path.join(app, 'dist', 'aos'));
  rmrf(backup);
  if (verbose) { rmrf(path.join(app, 'aos')); log('Оригинальные файлы возвращены, мод удалён.'); }
  else { const inst = path.join(app, 'aos', 'INSTALLED'); if (fs.existsSync(inst)) fs.unlinkSync(inst); }
  return true;
}

function status(app) {
  const inst = path.join(app, 'aos', 'INSTALLED');
  log(fs.existsSync(inst) ? 'Установлен: ' + read(inst) : 'Мод не установлен.');
}

function main() {
  const [cmd = 'install', arg] = process.argv.slice(2);
  const app = findAppDir(arg);
  if (!app) {
    console.error('Не нашёл папку драйвера. Укажи путь: patch.js ' + cmd + ' "C:\\...\\Attack Shark Driver"');
    process.exit(2);
  }
  log('Папка: ' + app);
  if (cmd === 'install') install(app);
  else if (cmd === 'uninstall') restore(app, true);
  else if (cmd === 'status') status(app);
  else { console.error('Команды: install | uninstall | status'); process.exit(2); }
}

try { main(); } catch (e) {
  console.error('\nОшибка: ' + (e && e.message ? e.message : e));
  if (e && (e.code === 'EPERM' || e.code === 'EACCES')) console.error('Нет прав на запись — запусти install.bat от имени администратора.');
  process.exit(1);
}
