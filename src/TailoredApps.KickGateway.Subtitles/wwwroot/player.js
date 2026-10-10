(() => {
  'use strict';

  // Delayed player: our own HLS (relayed by the gateway) positioned `delay` seconds behind the live
  // edge, with captions timed against #EXT-X-PROGRAM-DATE-TIME — the same clock the transcriber
  // stamps transcripts with — so a line shows when its words play, not when it arrived. Translations
  // arrive as `translation` events and replace the cue texts of their transcript.

  const SITE_LANG = 'pl';
  const CAPTION_LEAD_MS = 400;       // show a cue slightly before its start (reading time)
  const CAPTION_HOLD_MS = 1800;      // keep it a moment after its end
  const LATE_SHOW_MS = 4000;         // a cue that arrived after its time: flash it now for this long
  const IDLE_HIDE_MS = 9000;
  const HUD_HIDE_MS = 3000;
  const HISTORY_MAX = 200;
  const TICK_MS = 200;

  const slug = decodeURIComponent(location.pathname.split('/').filter(Boolean)[0] || '').toLowerCase();
  const params = new URLSearchParams(location.search);
  const token = params.get('token') || '';
  const cid = Math.random().toString(36).slice(2, 12);

  const $ = id => document.getElementById(id);
  const stage = $('stage'), video = $('video'), captions = $('captions'), prev = $('prev'), cur = $('cur');
  const status = $('status'), title = $('title'), behind = $('behind'), sync = $('sync'), hud = $('hud');
  const panel = $('panel'), log = $('log'), track = $('track'), delaySel = $('delay'), waiting = $('waiting'), waitingText = $('waiting-text');

  document.title = `${slug} · −${params.get('delay') || '?'} s · z napisami`;
  title.textContent = slug;

  const hlsBase = `/${encodeURIComponent(slug)}/hls/`;
  const q = `?token=${encodeURIComponent(token)}&cid=${cid}`;

  // ---- preferences (shared with the public page) ----
  const prefs = {
    get size() { return parseFloat(localStorage.getItem('captionSize') || params.get('size') || '2.2'); },
    set size(v) { localStorage.setItem('captionSize', String(v)); applySize(); },
    get tags() { return (localStorage.getItem('captionTags') ?? '1') === '1'; },
    set tags(v) { localStorage.setItem('captionTags', v ? '1' : '0'); captions.classList.toggle('no-tags', !v); },
    get track() { return localStorage.getItem('captionTrack') || 'auto'; },
    set track(v) { localStorage.setItem('captionTrack', v); track.value = v; lastRendered = ''; rerenderHistory(); },
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
  $('unmute').onclick = () => { video.muted = !video.muted; $('unmute').textContent = video.muted ? '🔇' : '🔊'; };
  video.addEventListener('volumechange', () => { $('unmute').textContent = video.muted ? '🔇' : '🔊'; });
  delaySel.onchange = () => {
    const u = new URL(location.href);
    u.searchParams.set('delay', delaySel.value);
    location.href = u.toString();
  };

  let hudTimer;
  function showHud() {
    hud.classList.remove('hidden');
    clearTimeout(hudTimer);
    hudTimer = setTimeout(() => hud.classList.add('hidden'), HUD_HIDE_MS);
  }
  ['mousemove', 'touchstart', 'keydown'].forEach(ev => document.addEventListener(ev, showHud, { passive: true }));
  showHud();

  // ---- events + cues ----
  const events = new Map();  // id -> latest event
  const cues = [];           // {eventId, index, start, end, original, lang, arrived, shownLate}
  const langCounts = new Map();
  function noteLanguage(lang) { if (lang) langCounts.set(lang, (langCounts.get(lang) || 0) + 1); }
  function mostCommonLanguage() {
    let best = '', n = 0;
    for (const [l, c] of langCounts) if (c > n) { best = l; n = c; }
    return best;
  }

  // Text for one cue under the current track: the translated segment with the same index when the
  // translation is aligned, the whole translation on the first cue when it is not, else the original.
  function cueText(c) {
    const ev = events.get(c.eventId);
    const t = prefs.track;
    const wants = ev && (t === SITE_LANG || (t === 'auto' && ev.language && ev.language !== SITE_LANG));
    if (wants && ev.translatedSegments && ev.translatedSegments[SITE_LANG]) {
      const segs = ev.translatedSegments[SITE_LANG];
      const count = cues.filter(x => x.eventId === c.eventId).length;
      if (segs.length === count && segs[c.index]) return { text: segs[c.index].text, translated: true };
      if (c.index === 0) return { text: ev.translations[SITE_LANG], translated: true };
      return { text: '', translated: true };
    }
    return { text: c.original, translated: false };
  }
  function tagHtml(lang, translated, from) {
    if (!lang) return '';
    const other = !translated && langCounts.size > 0 && lang !== mostCommonLanguage();
    const label = translated ? `${escapeHtml(from)}→${escapeHtml(lang)}` : escapeHtml(lang);
    return `<span class="tag ${other ? 'other' : ''} ${translated ? 'translated' : ''}">[${label}]</span>`;
  }

  function historyHtml(ev) {
    const time = new Date(ev.startedAt);
    const hh = String(time.getHours()).padStart(2, '0'), mm = String(time.getMinutes()).padStart(2, '0'), ss = String(time.getSeconds()).padStart(2, '0');
    const low = typeof ev.confidence === 'number' && ev.confidence > 0 && ev.confidence < 0.5 ? 'low' : '';
    const t = prefs.track;
    const wants = t === SITE_LANG || (t === 'auto' && ev.language && ev.language !== SITE_LANG);
    const translated = wants && ev.translations && ev.translations[SITE_LANG];
    const text = translated ? ev.translations[SITE_LANG] : ev.text;
    return `<time>${hh}:${mm}:${ss}</time>${tagHtml(translated ? SITE_LANG : ev.language, !!translated, ev.language)}<span class="${low}">${escapeHtml(text)}</span>`;
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
    const segs = (ev.segments && ev.segments.length) ? ev.segments : [{ startedAt: ev.startedAt, endedAt: ev.endedAt, text: ev.text }];
    const arrived = Date.now();
    segs.forEach((s, index) => cues.push({ eventId: ev.id, index, start: Date.parse(s.startedAt), end: Date.parse(s.endedAt), original: s.text, lang: ev.language, arrived, shownLate: 0 }));
    const cutoff = Date.now() - 10 * 60 * 1000;
    while (cues.length && cues[0].end < cutoff) cues.shift();
  }
  function onTranslation(ev) {
    events.set(ev.id, ev);
    enableTranslationTrack();
    const li = [...log.children].find(l => Number(l.dataset.id) === ev.id);
    if (li) li.innerHTML = historyHtml(ev);
    lastRendered = ''; // force a re-render on the next tick
  }

  const es = new EventSource(`/${encodeURIComponent(slug)}/events`);
  es.onopen = () => { status.className = 'dot live'; status.title = 'napisy: połączono'; };
  es.onerror = () => { status.className = 'dot connecting'; status.title = 'napisy: łączenie ponownie…'; };
  es.addEventListener('transcript', e => {
    try { onTranscript(JSON.parse(e.data)); } catch (err) { console.error('bad transcript event', err); }
  });
  es.addEventListener('translation', e => {
    try { onTranslation(JSON.parse(e.data)); } catch (err) { console.error('bad translation event', err); }
  });

  // ---- caption rendering driven by the video clock ----
  let idleTimer;
  let lastRendered = '';
  function renderLines(items) {
    const rendered = items.map(c => ({ c, t: cueText(c) })).filter(x => x.t.text);
    const key = rendered.map(x => x.t.text).join('\u0001');
    if (key === lastRendered) return;
    lastRendered = key;
    const render = (el, x) => {
      if (!x) { el.innerHTML = ''; el.classList.remove('late'); return; }
      el.innerHTML = tagHtml(x.t.translated ? SITE_LANG : x.c.lang, x.t.translated, x.c.lang) + escapeHtml(x.t.text);
      el.classList.toggle('late', !!x.c.late);
    };
    render(prev, rendered.length > 1 ? rendered[0] : null);
    render(cur, rendered[rendered.length - 1] || null);
    if (rendered.length) {
      captions.classList.remove('idle');
      clearTimeout(idleTimer);
      idleTimer = setTimeout(() => captions.classList.add('idle'), IDLE_HIDE_MS);
    }
  }

  let hls = null;
  function tick() {
    const pd = hls && hls.playingDate;
    const playingDateMs = pd ? pd.getTime() : null;
    const now = Date.now();

    if (playingDateMs) {
      behind.textContent = `obraz −${((now - playingDateMs) / 1000).toFixed(0)} s za live`;
      const active = [];
      for (const c of cues) {
        if (c.start <= playingDateMs + CAPTION_LEAD_MS && c.end + CAPTION_HOLD_MS >= playingDateMs) active.push(c);
        else if (c.end + CAPTION_HOLD_MS < playingDateMs && c.arrived > c.end && !c.shownLate) {
          c.shownLate = now; c.late = true; // arrived after its moment had already played
        }
        if (c.shownLate && now - c.shownLate < LATE_SHOW_MS && !active.includes(c)) active.push(c);
      }
      renderLines(active.slice(-2));
      const lateRecently = cues.some(c => c.shownLate && now - c.shownLate < 30000);
      sync.textContent = lateRecently ? 'napisy spóźnione — zwiększ opóźnienie' : 'napisy zsynchronizowane';
      sync.className = 'lag ' + (lateRecently ? 'sync-late' : 'sync-ok');
    } else {
      renderLines(cues.filter(c => now - c.arrived < 12000).slice(-2));
    }
  }
  setInterval(tick, TICK_MS);

  // ---- player ----
  async function fetchStatus() {
    const r = await fetch(hlsBase + 'status' + q, { cache: 'no-store' });
    if (r.status === 404) throw new Error('relay unavailable (token / channel)');
    return r.json();
  }

  async function start() {
    let st;
    try { st = await fetchStatus(); }
    catch (e) { waitingText.textContent = 'brak dostępu do relayu (token lub kanał)'; return; }

    const delay = Math.min(Math.max(parseInt(params.get('delay') || st.defaultDelaySeconds, 10) || st.defaultDelaySeconds, 0), st.maxDelaySeconds);
    delaySel.value = String([10, 15, 20, 30].includes(delay) ? delay : 15);
    document.title = `${slug} · −${delay} s · z napisami`;

    while (!st.ready) {
      waitingText.textContent = st.segments === 0 ? 'czekam na strumień…' : `buforuję… ${st.bufferedSeconds.toFixed(0)} s`;
      await new Promise(r => setTimeout(r, 2000));
      try { st = await fetchStatus(); } catch { }
    }

    if (!window.Hls || !Hls.isSupported()) {
      waitingText.textContent = 'ta przeglądarka nie obsługuje hls.js';
      return;
    }

    hls = new Hls({
      liveSyncDuration: delay,
      liveMaxLatencyDuration: delay + 10,
      liveDurationInfinity: true,
      maxBufferLength: 20,
      maxMaxBufferLength: 30,
      backBufferLength: 10,
      manifestLoadingMaxRetry: 8,
      levelLoadingMaxRetry: 8,
      fragLoadingMaxRetry: 6,
      enableWorker: true,
    });
    hls.on(Hls.Events.MANIFEST_PARSED, () => {
      waiting.classList.add('hidden');
      video.play().catch(() => { /* needs a gesture; the unmute button also triggers play */ });
    });
    hls.on(Hls.Events.ERROR, (_, data) => {
      if (data.response && data.response.code === 429) {
        waitingText.textContent = 'limit widzów relayu osiągnięty — spróbuj później';
        waiting.classList.remove('hidden');
        hls.destroy(); hls = null;
        return;
      }
      if (data.fatal) {
        waitingText.textContent = 'błąd odtwarzania — ponawiam…';
        waiting.classList.remove('hidden');
        setTimeout(() => { if (hls) hls.destroy(); hls = null; start(); }, 3000);
      }
    });
    hls.loadSource(hlsBase + 'playlist.m3u8' + q);
    hls.attachMedia(video);
    $('unmute').addEventListener('click', () => video.play().catch(() => {}), { once: true });
  }
  start();

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
  }
})();
