/*
 * AttackOnShadow — Persona 3 Reload theme for Attack Shark Driver.
 *
 * The stock UI is styled three ways: Emotion CSS classes (inserted at runtime),
 * React inline styles, and PNG icons baked in as data URLs. Instead of chasing
 * hashed class names, this script remaps the palette itself: every color that
 * reaches the page is converted from the stock teal/gray scheme to P3R blues.
 */
(function () {
  'use strict';

  // ---------- palette remap ----------
  var ACCENT_HUE = 193;   // stock accent #23FFE9 (hue ~174) -> P3R cyan-blue
  var NAVY_HUE = 224;     // stock background #35484C (teal gray) -> deep navy

  function rgbToHsl(r, g, b) {
    r /= 255; g /= 255; b /= 255;
    var max = Math.max(r, g, b), min = Math.min(r, g, b), h = 0, s = 0, l = (max + min) / 2;
    if (max !== min) {
      var d = max - min;
      s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
      if (max === r) h = (g - b) / d + (g < b ? 6 : 0);
      else if (max === g) h = (b - r) / d + 2;
      else h = (r - g) / d + 4;
      h *= 60;
    }
    return [h, s, l];
  }
  function hslToRgb(h, s, l) {
    h = ((h % 360) + 360) % 360 / 360;
    if (s === 0) { var v = Math.round(l * 255); return [v, v, v]; }
    var q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
    function f(t) {
      if (t < 0) t += 1; if (t > 1) t -= 1;
      if (t < 1 / 6) return p + (q - p) * 6 * t;
      if (t < 1 / 2) return q;
      if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
      return p;
    }
    return [Math.round(f(h + 1 / 3) * 255), Math.round(f(h) * 255), Math.round(f(h - 1 / 3) * 255)];
  }

  var cache = {};
  function mapRgba(r, g, b, a) {
    var key = r + ',' + g + ',' + b + ',' + a;
    if (cache[key]) return cache[key];
    var hsl = rgbToHsl(r, g, b), h = hsl[0], s = hsl[1], l = hsl[2], out;
    if (l <= 0.04 && a < 1) {
      // translucent black panels -> translucent navy glass
      out = [5, 16, 52, Math.min(0.85, a * 1.9)];
    } else if (l <= 0.04 || l >= 0.97) {
      out = [r, g, b, a]; // keep true black / white
    } else if (s < 0.1) {
      // neutral grays -> blue-tinted grays, brighter ones closer to ice white
      var nl = l < 0.5 ? l * 1.05 : l;
      out = hslToRgb(218, l < 0.5 ? 0.45 : 0.55, nl).concat(a);
    } else if (h >= 150 && h <= 210 && s < 0.45 && l < 0.45) {
      // stock teal-gray backgrounds -> deep navy
      out = hslToRgb(NAVY_HUE, 0.72, Math.max(0.07, l * 0.56)).concat(a);
    } else if (h >= 150 && h <= 200) {
      // stock teal accent -> P3R cyan
      out = hslToRgb(ACCENT_HUE, 1, Math.min(0.62, Math.max(0.5, l))).concat(a);
    } else {
      out = [r, g, b, a];
    }
    var res = out[3] >= 1 ? 'rgb(' + out[0] + ', ' + out[1] + ', ' + out[2] + ')'
      : 'rgba(' + out[0] + ', ' + out[1] + ', ' + out[2] + ', ' + (+out[3].toFixed(3)) + ')';
    cache[key] = res;
    return res;
  }

  var RE_HEX = /#([0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3})\b/g;
  var RE_RGB = /rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+%?)\s*)?\)/g;
  function remapText(css) {
    if (!css || css.indexOf('aos-keep') !== -1) return css;
    return css
      .replace(RE_RGB, function (m, r, g, b, a) {
        var al = a === undefined ? 1 : (a.slice(-1) === '%' ? parseFloat(a) / 100 : parseFloat(a));
        return mapRgba(+r, +g, +b, al);
      })
      .replace(RE_HEX, function (m, hex) {
        if (hex.length === 3) hex = hex[0] + hex[0] + hex[1] + hex[1] + hex[2] + hex[2];
        var a = hex.length === 8 ? parseInt(hex.slice(6), 16) / 255 : 1;
        return mapRgba(parseInt(hex.slice(0, 2), 16), parseInt(hex.slice(2, 4), 16), parseInt(hex.slice(4, 6), 16), a);
      });
  }
  window.__aosRemap = remapText;

  // Emotion (production) inserts rules through CSSStyleSheet.insertRule.
  var origInsert = CSSStyleSheet.prototype.insertRule;
  CSSStyleSheet.prototype.insertRule = function (rule, index) {
    var own = this.ownerNode && this.ownerNode.id === 'aos-theme-css';
    // The background-colour layer (a ::before at z-index -2) carries the colour the
    // user picked in Info -> Appearance: keep it exactly as chosen. The default
    // colour is replaced by the P3R background in aos-theme.css instead.
    var userBg = typeof rule === 'string' && rule.indexOf('::before') !== -1 && rule.indexOf('z-index:-2') !== -1;
    return origInsert.call(this, own || userBg ? rule : remapText(rule), index);
  };

  function fixStyleTag(node) {
    if (node.id === 'aos-theme-css' || node.__aosDone) return;
    node.__aosDone = true;
    var t = node.textContent;
    if (t) { var n = remapText(t); if (n !== t) node.textContent = n; }
  }

  function fixInline(el) {
    var s = el.getAttribute('style');
    if (!s) return;
    if (/background:\s*rgba\(0,\s*0,\s*0,\s*0\.3\)/.test(s)) el.classList.add('aos-panel');
    var n = remapText(s);
    if (n !== s) el.setAttribute('style', n);
  }

  // Tint teal PNG icons (checkboxes, toggles, edit icons) towards cyan.
  function tintImages(root) {
    var list = root.querySelectorAll ? root.querySelectorAll('img, div, span') : [];
    for (var i = 0; i < list.length; i++) {
      var el = list[i];
      if (el.__aosImg) continue;
      if (el.tagName === 'IMG') {
        el.__aosImg = true;
        var src = el.getAttribute('src') || '';
        // logos and device photos live in company/; only tint the stock UI icons
        if (src.indexOf('company') === -1 && el.width * el.height < 4096) el.classList.add('aos-icon');
        continue;
      }
      var bi = el.style && el.style.backgroundImage;
      if (!bi) { var cs = getComputedStyle(el); bi = cs.backgroundImage; }
      if (bi && bi !== 'none' && bi.indexOf('url(') !== -1 && !el.textContent.trim()) {
        el.__aosImg = true;
        el.classList.add('aos-icon');
      }
    }
  }

  // Mark active tabs so CSS can draw the P3R "selected plate".
  function markTabs() {
    var tabs = document.querySelectorAll('.css-1a65vc5, .css-4icp3k, .css-yyjjii');
    for (var i = 0; i < tabs.length; i++) {
      var t = tabs[i];
      // The stock UI marks the active tab with a visible bottom border. Our CSS
      // hides that border, so read it with our classes temporarily removed.
      var had = t.classList.contains('aos-tab-on');
      t.classList.remove('aos-tab', 'aos-tab-on');
      var cs = getComputedStyle(t);
      var on = cs.borderBottomStyle !== 'none' && cs.borderBottomWidth !== '0px' &&
        !/rgba\(\d+, \d+, \d+, 0\)/.test(cs.borderBottomColor);
      t.classList.add('aos-tab');
      if (on) t.classList.add('aos-tab-on');
      if (had !== on) t.__aosChanged = true;
    }
  }

  var pending = new Set(), scheduled = false;
  function flush() {
    scheduled = false;
    pending.forEach(function (el) {
      if (!el.isConnected) return;
      if (el.tagName === 'STYLE') { fixStyleTag(el); return; }
      fixInline(el);
      var all = el.querySelectorAll ? el.querySelectorAll('[style]') : [];
      for (var i = 0; i < all.length; i++) fixInline(all[i]);
      tintImages(el);
    });
    pending.clear();
    markTabs();
  }
  function queue(el) {
    pending.add(el);
    if (!scheduled) { scheduled = true; requestAnimationFrame(flush); }
  }

  var mo = new MutationObserver(function (muts) {
    for (var i = 0; i < muts.length; i++) {
      var m = muts[i];
      if (m.type === 'attributes') { queue(m.target); continue; }
      if (m.type === 'characterData') { var p = m.target.parentNode; if (p && p.tagName === 'STYLE') { p.__aosDone = false; queue(p); } continue; }
      for (var j = 0; j < m.addedNodes.length; j++) {
        var n = m.addedNodes[j];
        if (n.nodeType === 1) queue(n);
      }
    }
  });

  function start() {
    var styles = document.querySelectorAll('style');
    for (var i = 0; i < styles.length; i++) fixStyleTag(styles[i]);
    mo.observe(document.documentElement, { subtree: true, childList: true, attributes: true, attributeFilter: ['style'], characterData: true });
    queue(document.body);
    document.documentElement.classList.add('aos');
    // Tab selection is a className change, which the observer ignores on purpose.
    document.addEventListener('click', function () { setTimeout(markTabs, 0); setTimeout(markTabs, 120); }, true);
    setInterval(markTabs, 700);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
