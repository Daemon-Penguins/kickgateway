(() => {
  'use strict';

  const PLAYER_BASE = 'https://player.kick.com';
  const IDLE_HIDE_MS = 9000;      // captions fade this long after the last line
  const BACKLOG_AGE_MS = 25000;   // older lines from the backlog go to the history panel only
  const HUD_HIDE_MS = 3000;
  const HISTORY_MAX = 200;

  const slug = decodeURIComponent(location.pathname.split('/').filter(Boolean)[0] || '').toLowerCase();
  const params = new URLSearchParams(location.search);
  const overlay = params.get('overlay') === '1';

  const $ = id => document.getElementById(id);
  const stage = $('stage'), player = $('player'), captions = $('captions'), prev = $('prev'), cur = $('cur');
  const status = $('status'), title = $('title'), lag = $('lag'), hud = $('hud'), panel = $('panel'), log = $('log'), track = $('track');

  document.title = `${slug} · z napisami`;
  title.textContent = slug;
  if (overlay) document.body.classList.add('overlay');
  else player.src = `${PLAYER_BASE}/${encodeURIComponent(slug)}?muted=${params.get('muted') === 'false' ? 'false' : 'true'}&autoplay=true`;

  // ---- preferences ----
  const prefs = {
    get size() { return parseFloat(localStorage.getItem('captionSize') || params.get('size') || '2.2'); },
    set size(v) { localStorage.setItem('captionSize', String(v)); applySize(); },
    get tags() { return (localStorage.getItem('captionTags') ?? '1') === '1'; },
    set tags(v) { localStorage.setItem('captionTags', v ? '1' : '0'); captions.classList.toggle('no-tags', !v); },
    get track() { return localStorage.getItem('captionTrack') || 'original'; },
    set track(v) { localStorage.setItem('captionTrack', v); track.value = v; rerender(); },
  };
  function applySize() { document.documentElement.style.setProperty('--caption-size', prefs.size + 'vw'); }
  applySize();
  captions.classList.toggle('no-tags', !prefs.tags);
  track.value = prefs.track;

  $('bigger').onclick = () => { prefs.size = Math.min(6, +(prefs.size + 0.3).toFixed(1)); };
  $('smaller').onclick = () => { prefs.size = Math.max(1, +(prefs.size - 0.3).toFixed(1)); };
  $('tags').onclick = () => { prefs.tags = !prefs.tags; };
  $('history').onclick = () => panel.classList.toggle('hidden');
  $('close-panel').onclick = () => panel.classList.add('hidden');
  $('fullscreen').onclick = () => document.fullscreenElement ? document.exitFullscreen() : stage.requestFullscreen();
  track.onchange = () => { prefs.track = track.value; };

  // HUD fades when the mouse is still (never shown in overlay mode).
  let hudTimer;
  function showHud() {
    if (overlay) return;
    hud.classList.remove('hidden');
    clearTimeout(hudTimer);
    hudTimer = setTimeout(() => hud.classList.add('hidden'), HUD_HIDE_MS);
  }
  ['mousemove', 'touchstart', 'keydown'].forEach(ev => document.addEventListener(ev, showHud, { passive: true }));
  showHud();

  // ---- captions ----
  let recent = [];            // last two displayed events, oldest first
  let idleTimer;
  let lagEma = null;
  const languagesSeen = new Set();

  function textOf(ev) {
    const t = prefs.track;
    if (t !== 'original' && ev.translations && ev.translations[t]) return ev.translations[t];
    return ev.text;
  }
  function tagHtml(ev, fallbackLang) {
    const lang = ev.language || fallbackLang || '';
    if (!lang) return '';
    const other = languagesSeen.size > 0 && lang !== mostCommonLanguage();
    return `<span class="tag ${other ? 'other' : ''}">[${escapeHtml(lang)}]</span>`;
  }
  function render(el, ev) {
    if (!ev) { el.innerHTML = ''; return; }
    el.innerHTML = tagHtml(ev) + escapeHtml(textOf(ev));
  }
  function rerender() {
    render(prev, recent.length > 1 ? recent[0] : null);
    render(cur, recent[recent.length - 1]);
  }
  function show(ev) {
    recent.push(ev);
    if (recent.length > 2) recent.shift();
    rerender();
    captions.classList.remove('idle');
    clearTimeout(idleTimer);
    idleTimer = setTimeout(() => captions.classList.add('idle'), IDLE_HIDE_MS);
  }

  const langCounts = new Map();
  function noteLanguage(lang) {
    if (!lang) return;
    languagesSeen.add(lang);
    langCounts.set(lang, (langCounts.get(lang) || 0) + 1);
  }
  function mostCommonLanguage() {
    let best = '', n = 0;
    for (const [l, c] of langCounts) if (c > n) { best = l; n = c; }
    return best;
  }

  function noteLag(ev) {
    if (typeof ev.lagSeconds !== 'number') return;
    lagEma = lagEma === null ? ev.lagSeconds : lagEma * 0.7 + ev.lagSeconds * 0.3;
    lag.textContent = `napisy ~${lagEma.toFixed(0)} s za dźwiękiem`;
  }

  function addHistory(ev) {
    const li = document.createElement('li');
    const time = new Date(ev.startedAt);
    const hh = String(time.getHours()).padStart(2, '0'), mm = String(time.getMinutes()).padStart(2, '0'), ss = String(time.getSeconds()).padStart(2, '0');
    const conf = typeof ev.confidence === 'number' && ev.confidence > 0 && ev.confidence < 0.5 ? ' low' : '';
    li.innerHTML = `<time>${hh}:${mm}:${ss}</time>${tagHtml(ev)}<span class="${conf.trim()}">${escapeHtml(textOf(ev))}</span>`;
    log.appendChild(li);
    while (log.children.length > HISTORY_MAX) log.removeChild(log.firstChild);
    log.scrollTop = log.scrollHeight;
  }

  function onTranscript(ev) {
    noteLanguage(ev.language);
    if (ev.translations && ev.translations.pl) {
      const pl = track.querySelector('option[value="pl"]');
      pl.disabled = false;
      pl.textContent = 'polski';
    }
    addHistory(ev);
    const age = Date.now() - Date.parse(ev.receivedAt);
    if (age > BACKLOG_AGE_MS) return; // backlog: history only, don't flash old lines as current
    noteLag(ev);
    show(ev);
  }

  // ---- SSE ----
  const es = new EventSource(`/${encodeURIComponent(slug)}/events`);
  es.onopen = () => { status.className = 'dot live'; status.title = 'połączono'; };
  es.onerror = () => { status.className = 'dot connecting'; status.title = 'łączenie ponownie…'; };
  es.addEventListener('transcript', e => {
    try { onTranscript(JSON.parse(e.data)); } catch (err) { console.error('bad transcript event', err); }
  });

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
  }
})();
