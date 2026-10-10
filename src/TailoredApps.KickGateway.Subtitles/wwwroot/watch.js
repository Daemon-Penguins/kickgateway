(() => {
  'use strict';

  const PLAYER_BASE = 'https://player.kick.com';
  const SITE_LANG = 'pl';             // "auto" track: translate into this when a line is in another language
  const IDLE_HIDE_MS = 9000;          // captions fade this long after the last line
  const BACKLOG_AGE_MS = 25000;       // older lines from the backlog go to the history panel only
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
    get track() { return localStorage.getItem('captionTrack') || 'auto'; },
    set track(v) { localStorage.setItem('captionTrack', v); track.value = v; rerender(); rerenderHistory(); },
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

  // ---- state ----
  const events = new Map();   // id -> latest event (translations merge in)
  let recent = [];            // ids of the last two displayed events, oldest first
  let idleTimer;
  let lagEma = null;
  const langCounts = new Map();

  function noteLanguage(lang) { if (lang) langCounts.set(lang, (langCounts.get(lang) || 0) + 1); }
  function mostCommonLanguage() {
    let best = '', n = 0;
    for (const [l, c] of langCounts) if (c > n) { best = l; n = c; }
    return best;
  }

  // Which text to show for an event under the current track; null language = original.
  function pick(ev) {
    const t = prefs.track;
    const wantsTranslation = t === SITE_LANG || (t === 'auto' && ev.language && ev.language !== SITE_LANG);
    if (wantsTranslation && ev.translations && ev.translations[SITE_LANG])
      return { text: ev.translations[SITE_LANG], lang: SITE_LANG, translated: true, from: ev.language };
    return { text: ev.text, lang: ev.language, translated: false };
  }
  function tagHtml(lang, translated, from) {
    if (!lang) return '';
    const other = !translated && langCounts.size > 0 && lang !== mostCommonLanguage();
    const label = translated ? `${escapeHtml(from)}→${escapeHtml(lang)}` : escapeHtml(lang);
    return `<span class="tag ${other ? 'other' : ''} ${translated ? 'translated' : ''}">[${label}]</span>`;
  }
  function render(el, ev) {
    if (!ev) { el.innerHTML = ''; return; }
    const p = pick(ev);
    el.innerHTML = tagHtml(p.lang, p.translated, p.from) + escapeHtml(p.text);
  }
  function rerender() {
    render(prev, recent.length > 1 ? events.get(recent[0]) : null);
    render(cur, recent.length ? events.get(recent[recent.length - 1]) : null);
  }
  function show(ev) {
    if (!recent.includes(ev.id)) recent.push(ev.id);
    if (recent.length > 2) recent.shift();
    rerender();
    captions.classList.remove('idle');
    clearTimeout(idleTimer);
    idleTimer = setTimeout(() => captions.classList.add('idle'), IDLE_HIDE_MS);
  }

  function noteLag(ev) {
    if (typeof ev.lagSeconds !== 'number') return;
    lagEma = lagEma === null ? ev.lagSeconds : lagEma * 0.7 + ev.lagSeconds * 0.3;
    lag.textContent = `napisy ~${lagEma.toFixed(0)} s za dźwiękiem`;
  }

  function historyHtml(ev) {
    const time = new Date(ev.startedAt);
    const hh = String(time.getHours()).padStart(2, '0'), mm = String(time.getMinutes()).padStart(2, '0'), ss = String(time.getSeconds()).padStart(2, '0');
    const low = typeof ev.confidence === 'number' && ev.confidence > 0 && ev.confidence < 0.5 ? 'low' : '';
    const p = pick(ev);
    return `<time>${hh}:${mm}:${ss}</time>${tagHtml(p.lang, p.translated, p.from)}<span class="${low}">${escapeHtml(p.text)}</span>`;
  }
  function addHistory(ev) {
    const li = document.createElement('li');
    li.dataset.id = ev.id;
    li.innerHTML = historyHtml(ev);
    log.appendChild(li);
    while (log.children.length > HISTORY_MAX) log.removeChild(log.firstChild);
    log.scrollTop = log.scrollHeight;
  }
  function rerenderHistory() {
    for (const li of log.children) {
      const ev = events.get(Number(li.dataset.id));
      if (ev) li.innerHTML = historyHtml(ev);
    }
  }

  function enableTranslationTrack() {
    const opt = track.querySelector(`option[value="${SITE_LANG}"]`);
    if (opt && opt.disabled) { opt.disabled = false; opt.textContent = 'polski'; }
  }

  function onTranscript(ev) {
    events.set(ev.id, ev);
    noteLanguage(ev.language);
    if (ev.translations && ev.translations[SITE_LANG]) enableTranslationTrack();
    addHistory(ev);
    const age = Date.now() - Date.parse(ev.receivedAt);
    if (age > BACKLOG_AGE_MS) return; // backlog: history only, don't flash old lines as current
    noteLag(ev);
    show(ev);
  }

  function onTranslation(ev) {
    events.set(ev.id, ev);
    enableTranslationTrack();
    const li = [...log.children].find(l => Number(l.dataset.id) === ev.id);
    if (li) li.innerHTML = historyHtml(ev);
    if (recent.includes(ev.id)) rerender();
  }

  // ---- SSE ----
  const es = new EventSource(`/${encodeURIComponent(slug)}/events`);
  es.onopen = () => { status.className = 'dot live'; status.title = 'połączono'; };
  es.onerror = () => { status.className = 'dot connecting'; status.title = 'łączenie ponownie…'; };
  es.addEventListener('transcript', e => {
    try { onTranscript(JSON.parse(e.data)); } catch (err) { console.error('bad transcript event', err); }
  });
  es.addEventListener('translation', e => {
    try { onTranslation(JSON.parse(e.data)); } catch (err) { console.error('bad translation event', err); }
  });

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
  }
})();
