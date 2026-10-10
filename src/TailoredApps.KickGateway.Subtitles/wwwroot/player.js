(() => {
  'use strict';

  // Delayed player: our own HLS (relayed by the gateway) positioned `delay` seconds behind the live
  // edge, with captions timed against #EXT-X-PROGRAM-DATE-TIME — the same clock the transcriber
  // stamps transcripts with — so a line shows when its words play, not when it arrived.

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
    get track() { return localStorage.getItem('captionTrack') || 'original'; },
    set track(v) { localStorage.setItem('captionTrack', v); track.value = v; },
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

  // ---- cues from SSE ----
  const cues = [];          // {start, end, text, lang, arrived, eventId, shownLate}
  const langCounts = new Map();
  function noteLanguage(lang) { if (lang) langCounts.set(lang, (langCounts.get(lang) || 0) + 1); }
  function mostCommonLanguage() {
    let best = '', n = 0;
    for (const [l, c] of langCounts) if (c > n) { best = l; n = c; }
    return best;
  }
  function textOf(ev, fallback) {
    const t = prefs.track;
    if (t !== 'original' && ev.translations && ev.translations[t]) return ev.translations[t];
    return fallback;
  }
  function tagHtml(lang) {
    if (!lang) return '';
    const other = langCounts.size > 0 && lang !== mostCommonLanguage();
    return `<span class="tag ${other ? 'other' : ''}">[${escapeHtml(lang)}]</span>`;
  }

  function addHistory(ev) {
    const li = document.createElement('li');
    const time = new Date(ev.startedAt);
    const hh = String(time.getHours()).padStart(2, '0'), mm = String(time.getMinutes()).padStart(2, '0'), ss = String(time.getSeconds()).padStart(2, '0');
    const low = typeof ev.confidence === 'number' && ev.confidence > 0 && ev.confidence < 0.5 ? 'low' : '';
    li.innerHTML = `<time>${hh}:${mm}:${ss}</time>${tagHtml(ev.language)}<span class="${low}">${escapeHtml(textOf(ev, ev.text))}</span>`;
    log.appendChild(li);
    while (log.children.length > HISTORY_MAX) log.removeChild(log.firstChild);
    log.scrollTop = log.scrollHeight;
  }

  function onTranscript(ev) {
    noteLanguage(ev.language);
    if (ev.translations && ev.translations.pl) {
      const pl = track.querySelector('option[value="pl"]');
      pl.disabled = false; pl.textContent = 'polski';
    }
    addHistory(ev);
    const segs = (ev.segments && ev.segments.length) ? ev.segments : [{ startedAt: ev.startedAt, endedAt: ev.endedAt, text: ev.text }];
    const arrived = Date.now();
    for (const s of segs) {
      cues.push({ start: Date.parse(s.startedAt), end: Date.parse(s.endedAt), text: textOf(ev, s.text), lang: ev.language, arrived, eventId: ev.id, shownLate: 0 });
    }
    // Keep only what can still matter (a few minutes).
    const cutoff = Date.now() - 10 * 60 * 1000;
    while (cues.length && cues[0].end < cutoff) cues.shift();
  }

  const es = new EventSource(`/${encodeURIComponent(slug)}/events`);
  es.onopen = () => { status.className = 'dot live'; status.title = 'napisy: połączono'; };
  es.onerror = () => { status.className = 'dot connecting'; status.title = 'napisy: łączenie ponownie…'; };
  es.addEventListener('transcript', e => {
    try { onTranscript(JSON.parse(e.data)); } catch (err) { console.error('bad transcript event', err); }
  });

  // ---- caption rendering driven by the video clock ----
  let idleTimer;
  let lastRendered = '';
  function renderLines(items) {
    const key = items.map(i => i.text).join('\u0001');
    if (key === lastRendered) return;
    lastRendered = key;
    const render = (el, cue) => {
      if (!cue) { el.innerHTML = ''; el.classList.remove('late'); return; }
      el.innerHTML = tagHtml(cue.lang) + escapeHtml(cue.text);
      el.classList.toggle('late', !!cue.late);
    };
    render(prev, items.length > 1 ? items[0] : null);
    render(cur, items[items.length - 1] || null);
    if (items.length) {
      captions.classList.remove('idle');
      clearTimeout(idleTimer);
      idleTimer = setTimeout(() => captions.classList.add('idle'), IDLE_HIDE_MS);
    }
  }

  let hls = null;
  let playingDateMs = null;
  function tick() {
    const pd = hls && hls.playingDate;
    playingDateMs = pd ? pd.getTime() : null;
    const now = Date.now();

    if (playingDateMs) {
      behind.textContent = `obraz −${((now - playingDateMs) / 1000).toFixed(0)} s za live`;
      // Cues whose window covers the playhead, in order; plus cues that arrived too late for their window.
      const active = [];
      let late = 0;
      for (const c of cues) {
        if (c.start <= playingDateMs + CAPTION_LEAD_MS && c.end + CAPTION_HOLD_MS >= playingDateMs) active.push(c);
        else if (c.end + CAPTION_HOLD_MS < playingDateMs && c.arrived > c.end && !c.shownLate) {
          // Arrived after its moment had already played (delay shorter than transcription latency).
          c.shownLate = now; c.late = true; late++;
        }
        if (c.shownLate && now - c.shownLate < LATE_SHOW_MS) active.push(c);
      }
      const items = active.slice(-2);
      renderLines(items);
      const lateRecently = cues.some(c => c.shownLate && now - c.shownLate < 30000);
      sync.textContent = lateRecently ? 'napisy spóźnione — zwiększ opóźnienie' : 'napisy zsynchronizowane';
      sync.className = 'lag ' + (lateRecently ? 'sync-late' : 'sync-ok');
    } else {
      // No program-date-time yet: fall back to "show as it arrives".
      const recent = cues.filter(c => now - c.arrived < 12000).slice(-2);
      renderLines(recent);
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
