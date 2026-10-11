// optima save: right-click a video (or press the button, alt+shift+s) → the windows save dialog → done.
// The helper (optima-save.exe, over native messaging) asks where to save, trims if you ticked "trim
// before saving", runs yt-dlp and plays the done sound. This file works out which video you meant and
// shows progress on the toolbar button. One file for Zen / Firefox (MV2 page) and Chrome / Brave /
// Edge (MV3 service worker), so it uses no DOM: OffscreenCanvas for the icon, no Audio.
//
// Security: permissions are only menus/contextMenus, nativeMessaging, activeTab and scripting (which
// adds no access of its own: it works only where activeTab already allows it). No access to any site until you click, no storage, no
// network. Everything read from a page is untrusted text: urls must be http(s), names are capped and
// only ever shown as plain text, and the helper re-checks all of it anyway.
'use strict';

const api = typeof browser !== 'undefined' ? browser : chrome;
const menus = api.menus || api.contextMenus;
const action = api.browserAction || api.action;
// Chrome now answers to `browser` too; only Firefox-family browsers have getBrowserInfo.
const isFirefox = typeof browser !== 'undefined' && !!browser.runtime && typeof browser.runtime.getBrowserInfo === 'function';

const HOST = 'optima_save';
const VIDEO = /youtube\.com\/(watch\?|shorts\/|live\/|embed\/)|youtu\.be\/|tiktok\.com\/(@[^/]*\/(video|photo)\/\d|t\/|embed)|vm\.tiktok\.com\/|instagram\.com\/([^/]+\/)?(reels?|p|tv)\/[\w-]|(x|twitter)\.com\/[^/]+\/status\/\d|vimeo\.com\/(\d|video\/)|reddit\.com\/r\/[^/]+\/comments\/|v\.redd\.it\/|facebook\.com\/(watch|reel|[^/]+\/videos\/)|fb\.watch\/|twitch\.tv\/([^/]+\/clip\/|videos\/)|clips\.twitch\.tv\/|dailymotion\.com\/video\/|soundcloud\.com\/[^/]+\/[^/?#]+|bandcamp\.com\/track\/|\.(mp4|webm|mov|m4v|mp3|m4a)(\?|$)/i;

const jobs = new Map(); // id → { port, url, title, name, percent, status }
let seq = 0;
let flash = null;       // the last save that ended: { done, path, name, message, url, title, until }

// One item, so it stays top level (two items would fold into an "optima save ›" submenu).
const item = { id: 'save', title: 'save video as…', contexts: ['page', 'link', 'video', 'audio', 'frame'] };
if (isFirefox) item.icons = { 16: 'menu.svg', 32: 'menu.svg' };
Promise.resolve(menus.removeAll()).catch(() => {}).then(() => menus.create(item, () => void api.runtime.lastError));
menus.onClicked.addListener((info, tab) => { if (info.menuItemId === 'save') begin(tab, info); });

action.setBadgeBackgroundColor({ color: '#2a47c4' });
if (action.setBadgeTextColor) action.setBadgeTextColor({ color: '#ffffff' });
action.onClicked.addListener((tab, click) => {
  if (click && click.button === 1) return cancelAll();                       // middle-click cancels (Firefox)
  if (flash && Date.now() < flash.until) {
    const f = flash;
    flash = null;
    if (f.done) return reveal(f.path);                                       // the check: show it
    if (f.url) return save(f.url, f.title, f.tabId, f.hint, f.private);      // the !: try again
    return render();
  }
  begin(tab, null);
});
if (typeof matchMedia === 'function') matchMedia('(prefers-color-scheme: dark)').addEventListener('change', render);
render();

// ------------------------------------------------------------------ which video

// Any slip on the way to the helper ends as a visible notice, never as silence.
function begin(tab, info) {
  start(tab, info).catch((e) => {
    console.error('optima save:', e);
    end({ done: false, message: 'something went wrong (' + (e && e.message) + ')', tabId: tab && tab.id });
  });
}

async function start(tab, info) {
  // Zen often hands a menu click over with no tab (and so no page access). Then it falls back to the
  // active tab's id and to the urls the menu itself carries; the helper looks up the title instead.
  if (!tab) [tab] = await api.tabs.query({ active: true, currentWindow: true }).catch(() => []);
  const tabId = tab ? tab.id : null;

  let found = null;
  if (tabId != null) {
    const target = info && info.targetElementId != null ? info.targetElementId : null;
    found = await inPage(tabId, info ? info.frameId : 0, finder, [target, VIDEO.source, VIDEO.flags]).catch(() => null);
  }

  if (!found || typeof found !== 'object') found = null;
  let url = found && safeUrl(found.url) ? found.url : null;
  if (!url && info) url = [info.linkUrl, info.frameUrl, info.srcUrl].find((u) => safeUrl(u) && VIDEO.test(u));
  if (!url) url = (info && info.pageUrl) || (tab && tab.url);
  if (!safeUrl(url)) return end({ done: false, message: "there's no video on this page to save", tabId });

  const fromPage = !found || found.page;
  const text = (v) => (typeof v === 'string' ? v.slice(0, 500) : '');
  const title = cleanTitle(text(found && found.title)) || (fromPage && tab ? cleanTitle(tab.title) : '');
  const hint = [text(found && found.author).slice(0, 40), site(url), stamp()].filter(Boolean).join(' ');
  save(url, title.slice(0, 120), tabId, hint, !!(tab && tab.incognito));
}

// Runs one of this file's functions inside the page (only possible with the one-tab access a click
// grants). It passes the function itself and plain JSON arguments: no code is ever built from text.
async function inPage(tabId, frameId, fn, args) {
  const [r] = await api.scripting.executeScript({ target: { tabId, frameIds: [frameId || 0] }, func: fn, args });
  return r && r.result;
}

function safeUrl(u) {
  if (typeof u !== 'string' || u.length > 4096 || /[\s\x00-\x1f]/.test(u)) return false;
  try { return /^https?:$/.test(new URL(u).protocol); } catch (e) { return false; }
}

// Runs inside the page. Returns { url, title, author, page } for the video the person meant.
function finder(targetId, videoSource, videoFlags) {
  const VIDEO = new RegExp(videoSource, videoFlags);
  const canon = (h) => {
    let m;
    if ((m = /[?&]v=([\w-]{11})/.exec(h)) || (m = /(?:shorts|live|embed)\/([\w-]{11})|youtu\.be\/([\w-]{11})/.exec(h)))
      return { key: 'yt' + (m[1] || m[2]), url: 'https://www.youtube.com/watch?v=' + (m[1] || m[2]) };
    if ((m = /instagram\.com\/(?:[^/]+\/)?(?:reels?|p|tv)\/([\w-]+)/.exec(h)))
      return { key: 'ig' + m[1], url: 'https://www.instagram.com/p/' + m[1] + '/' };
    if ((m = /tiktok\.com\/@([^/]*)\/(?:video|photo)\/(\d+)/.exec(h)))
      return { key: 'tt' + m[2], url: 'https://www.tiktok.com/@' + m[1] + '/video/' + m[2] };
    if ((m = /(?:x|twitter)\.com\/([^/]+)\/status\/(\d+)/.exec(h)))
      return { key: 'x' + m[2], url: 'https://x.com/' + m[1] + '/status/' + m[2] };
    return { key: h.split('#')[0], url: h.split('#')[0] };
  };
  const clean = (t) => (t || '').replace(/\s+/g, ' ').trim();
  // The best name among the links to the same video: a title attribute, else the longest real text.
  const titleFor = (key, scope) => {
    let byAttr = '', byText = '';
    for (const a of (scope || document).querySelectorAll('a[href]')) {
      if (canon(a.href).key !== key) continue;
      const attr = clean(a.getAttribute('title')), txt = clean(a.textContent);
      if (attr.length > byAttr.length) byAttr = attr;
      if (txt.length > byText.length && txt.length < 200 && !/^[\d:]+(\s|$)/.test(txt)) byText = txt;
    }
    return byAttr || byText;
  };
  // The account name on instagram / tiktok, for a better file name than "video".
  const authorIn = (scope) => {
    for (const a of scope.querySelectorAll('a[href]')) {
      const m = /instagram\.com\/([\w.]+)\/$/.exec(a.href) || /tiktok\.com\/@([\w.]+)\/?$/.exec(a.href);
      if (m && !/^(explore|reels?|p|direct|accounts)$/.test(m[1])) return '@' + m[1];
    }
    return '';
  };
  const videosIn = (n) => {
    const out = new Map();
    for (const a of n.querySelectorAll('a[href]')) if (VIDEO.test(a.href)) { const c = canon(a.href); out.set(c.key, c); }
    if (/tiktok\.com$/.test(location.hostname)) // the feed has no link per video; the id sits in element ids
      for (const el of n.querySelectorAll('[id]')) {
        const m = /(?:^|\D)(\d{19})(?:\D|$)/.exec(el.id);
        if (m) out.set('tt' + m[1], { key: 'tt' + m[1], url: 'https://www.tiktok.com/@/video/' + m[1] });
      }
    return [...out.values()];
  };

  // Firefox can name the exact element that was right-clicked; Chrome can't, so it goes by the video.
  const getTarget = typeof browser !== 'undefined' && browser.menus && browser.menus.getTargetElement;
  let el = targetId != null && getTarget ? getTarget(targetId) : null;
  if (!el) { // the button, the shortcut, or Chrome: the video taking up most of the screen
    let most = 0;
    for (const v of document.querySelectorAll('video')) {
      const r = v.getBoundingClientRect();
      const area = Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0)) * Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0));
      if (area > most) { most = area; el = v; }
    }
  }
  // 1. right-clicked a link to a video (a thumbnail, a title)
  const link = el && el.closest && el.closest('a[href]');
  if (link && VIDEO.test(link.href)) { const c = canon(link.href); return { url: c.url, title: titleFor(c.key) }; }
  // 2. this page is the video
  if (VIDEO.test(location.href)) return { url: canon(location.href).url, page: true };
  // 3. a feed: the smallest box around the spot that holds exactly one video
  for (let n = el && el.parentElement, i = 0; n && n !== document.documentElement && i < 30; n = n.parentElement, i++) {
    const v = videosIn(n);
    if (v.length === 1) return { url: v[0].url, title: titleFor(v[0].key, n), author: authorIn(n) };
    if (v.length > 1) break;
  }
  return null;
}

function cleanTitle(t) {
  if (!t) return '';
  t = t.replace(/^\(\d+\+?\)\s*/, '');                                     // "(3) " unread count
  const ig = /on Instagram:\s*["“]([\s\S]*)["”]\s*$/.exec(t);
  if (ig) t = ig[1];
  t = t.replace(/\s*[-|•·/]\s*(YouTube|TikTok|Instagram|Vimeo|Reddit|X|Twitch|Facebook|SoundCloud)\b.*$/i, '');
  if (/^(Instagram|TikTok.*Make Your Day|TikTok|YouTube|X|Reels?|Explore)$/i.test(t.trim())) return '';
  t = t.split('\n')[0].replace(/#[\p{L}\p{N}_]+/gu, '').replace(/\s+/g, ' ').trim();
  if (t.length > 90) t = t.slice(0, 90).replace(/\s+\S*$/, '');
  return t;
}

function site(url) {
  try {
    const h = new URL(url).hostname.replace(/^(www|m|vm)\./, '');
    return h.includes('youtu') ? 'youtube' : h.split('.').slice(-2, -1)[0] || h;
  } catch (e) { return 'video'; }
}

function stamp() {
  const d = new Date(), p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}${p(d.getMinutes())}`;
}

// ------------------------------------------------------------------ talking to the helper

// A save from a private window tells the helper so, and it then writes nothing but the file itself.
function save(url, title, tabId, hint, isPrivate) {
  // Windows can hold a just-updated helper for a few seconds while it checks it; repeated clicks on the
  // same video during that wait must not pile up into several save dialogs.
  for (const j of jobs.values()) if (j.url === url) return render();
  const job = { id: ++seq, url, title, tabId, hint, private: isPrivate, name: title || hint, percent: null, status: 'asking' };
  job.port = api.runtime.connectNative(HOST);
  jobs.set(job.id, job);
  job.port.onMessage.addListener((m) => onMessage(job, m));
  job.port.onDisconnect.addListener((p) => {
    const error = (p && p.error) || api.runtime.lastError;
    if (!jobs.has(job.id)) return;
    jobs.delete(job.id);
    const missing = error && /no such native application|not found|manifest/i.test(error.message);
    if (error) console.error('optima save: helper connection failed:', error.message);
    end({ done: false, url, title, tabId, hint, private: isPrivate, message: missing ? "the helper isn't installed. double-click optima-save.exe once" : (error ? error.message : 'the helper stopped') });
  });
  job.port.postMessage({ action: 'save', url, title, hint, private: !!isPrivate });
  render();
}

function onMessage(job, m) {
  if (!m || typeof m !== 'object') return;
  if (m.type === 'start') Object.assign(job, { name: String(m.name || job.name), status: 'saving', percent: 0 });
  else if (m.type === 'setup' || m.type === 'progress') Object.assign(job, { percent: Number(m.percent) || 0, status: String(m.status || 'saving') });
  else if (m.type === 'trimming') Object.assign(job, { status: 'asking' });           // the trim window is open
  else {
    jobs.delete(job.id);
    job.port.disconnect();
    if (m.type === 'done') end({ done: true, path: String(m.path), name: job.name });
    if (m.type === 'error') end({ done: false, url: job.url, title: job.title, tabId: job.tabId, hint: job.hint, private: job.private, name: job.name, message: String(m.message) });
    // 'cancelled': the dialog or the trim window was closed, nothing to show
  }
  render();
}

function end(f) {
  if (!f.done) notice(f.tabId, "couldn't save: " + f.message);
  flash = Object.assign(f, { until: Date.now() + (f.done ? 8000 : 20000) });
  setTimeout(render, f.done ? 8050 : 20050);
  render();
}

function cancelAll() {
  for (const job of jobs.values()) if (job.status !== 'asking') { job.port.disconnect(); jobs.delete(job.id); }
  render();
}

function reveal(path) {
  Promise.resolve(api.runtime.sendNativeMessage(HOST, { action: 'reveal', path })).catch(() => {});
  render();
}

// A small plastic notice on the page you clicked, so a problem shows even when the toolbar button is
// tucked away. Plain text only, inside a closed shadow root the page can't read or restyle. Uses the
// one-tab access your click already granted (activeTab); nothing new.
function notice(tabId, message) {
  if (tabId == null) return;
  inPage(tabId, 0, showNotice, [String(message).slice(0, 220)]).catch(() => {});
}

function showNotice(message) {
  const old = document.getElementById('optima-save-notice');
  if (old) old.remove();
  const host = document.createElement('div');
  host.id = 'optima-save-notice';
  host.style.cssText = 'all:initial;position:fixed;z-index:2147483647;left:50%;bottom:28px;transform:translateX(-50%)';
  const root = host.attachShadow({ mode: 'closed' });
  const pill = document.createElement('div');
  pill.style.cssText = 'display:flex;gap:10px;align-items:center;max-width:min(560px,90vw);padding:10px 18px 10px 14px;'
    + 'font:13px/1.35 Inter,"Segoe UI",system-ui,sans-serif;color:#2b2926;background:linear-gradient(145deg,#efebe6,#dcd7cf);'
    + 'border:1.4px solid #34312d;border-radius:999px;box-shadow:-4px -4px 9px rgba(255,255,255,.6),4px 4px 12px rgba(0,0,0,.28)';
  const dot = document.createElement('span');
  dot.style.cssText = 'flex:none;width:8px;height:8px;border-radius:50%;background:#d98a2b';
  const text = document.createElement('span');
  text.textContent = 'optima save · ' + message;
  pill.append(dot, text);
  root.append(pill);
  document.documentElement.append(host);
  setTimeout(() => host.remove(), 8000);
}

// ------------------------------------------------------------------ the button

function render() {
  const busy = [...jobs.values()].filter((j) => j.status !== 'asking');
  let state, tip, badge = '';
  if (busy.length) {
    const known = busy.filter((j) => j.percent != null && j.percent >= 0);
    state = { kind: 'busy', pct: known.length ? known.reduce((s, j) => s + j.percent, 0) / known.length : 0 };
    if (busy.length > 1) badge = String(busy.length);
    tip = busy.map((j) => `${j.status} · ${j.name}${j.percent > 0 ? ' · ' + Math.round(j.percent) + '%' : ''}`).join('\n') + (isFirefox ? '\n(middle-click to cancel)' : '');
  } else if (flash && Date.now() < flash.until) {
    state = { kind: flash.done ? 'done' : 'error' };
    tip = flash.done ? `saved: ${flash.name}\nclick to show it in its folder` : `couldn't save: ${flash.message}\nclick to try again`;
  } else {
    flash = null;
    state = { kind: 'idle' };
    tip = 'save video as… (alt+shift+s)';
  }
  try { action.setIcon({ imageData: { 16: draw(16, state), 32: draw(32, state) } }); } catch (e) { /* keeps the file icon */ }
  action.setBadgeText({ text: badge });
  action.setTitle({ title: tip });
}

// The icon is drawn, not a file, so the ring can fill up blue as the save goes.
function draw(size, { kind, pct }) {
  const c = new OffscreenCanvas(size, size);
  const g = c.getContext('2d');
  g.scale(size / 16, size / 16);
  // A service worker can't see the theme; a mid grey reads on light and dark toolbars alike.
  const dark = typeof matchMedia === 'function' ? matchMedia('(prefers-color-scheme: dark)').matches : null;
  const ink = dark === null ? '#8e877e' : dark ? '#e7e3dd' : '#2b2926';
  const blue = dark ? '#8aa0ff' : '#3a5ad8', amber = '#d98a2b';
  g.lineWidth = 1.6;
  g.lineCap = g.lineJoin = 'round';
  const line = (color, ...pts) => {
    g.strokeStyle = color;
    g.beginPath();
    pts.forEach(([x, y], i) => (i ? g.lineTo(x, y) : g.moveTo(x, y)));
    g.stroke();
  };
  const ring = (color, from, to) => { g.strokeStyle = color; g.beginPath(); g.arc(8, 8, 6.3, from, to); g.stroke(); };
  if (kind === 'busy') {
    g.globalAlpha = 0.28; ring(ink, 0, Math.PI * 2); g.globalAlpha = 1;
    if (pct > 0) ring(blue, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * Math.min(pct, 100) / 100);
    line(ink, [8, 4.9], [8, 10.4]);
    line(ink, [5.9, 8.3], [8, 10.4], [10.1, 8.3]);
  } else if (kind === 'done') {
    line(blue, [3.4, 8.6], [6.5, 11.6], [12.6, 4.6]);
  } else if (kind === 'error') {
    line(amber, [8, 2.8], [8, 9.4]);
    g.fillStyle = amber; g.beginPath(); g.arc(8, 12.9, 1.15, 0, Math.PI * 2); g.fill();
  } else {
    line(ink, [8, 2.2], [8, 9.6]);
    line(ink, [4.9, 6.6], [8, 9.7], [11.1, 6.6]);
    line(ink, [2.6, 10.6], [2.6, 13.4], [13.4, 13.4], [13.4, 10.6]);
  }
  return g.getImageData(0, 0, size, size);
}
