/*
 * Kick clips OBS player.
 *
 * Plays a channel's clips full-bleed for an OBS Browser source: the two latest
 * clips first, then random picks forever. Deliberately a plain static page (no
 * Blazor circuit) so it survives running for hours/days in OBS.
 *
 * URL: /obs/clips/{slug}   (slug read from the path)
 * Params:
 *   ?muted=1     start muted (default: audio on — OBS allows autoplay with sound)
 *   ?overlay=1   show a title/creator lower-third at the start of each clip
 *   ?refresh=N   minutes between background clip-list refreshes (default 10)
 */
(function () {
    "use strict";

    var video = document.getElementById("video");
    var overlayEl = document.getElementById("overlay");
    var msgEl = document.getElementById("msg");

    var params = new URLSearchParams(location.search);
    var wantMuted = params.get("muted") === "1" || params.get("muted") === "true";
    var showOverlay = params.get("overlay") === "1" || params.get("overlay") === "true";
    var refreshMin = parseFloat(params.get("refresh")) || 10;

    var slug = (params.get("channel") || lastPathSegment() || "").trim().toLowerCase();

    // --- playback state ---
    var pool = [];                 // newest-first clips from the API
    var knownIds = new Set();      // every clip id we've seen (refresh diffing)
    var priority = [];             // clips to play next (latest two, then freshly-found)
    var bag = [];                  // shuffled index queue for the random phase
    var lastPlayedId = null;
    var fails = 0;
    var hls = null;
    var stallTimer = null;

    function lastPathSegment() {
        var parts = location.pathname.split("/").filter(Boolean);
        return parts.length ? decodeURIComponent(parts[parts.length - 1]) : "";
    }

    // ---------- clip selection ----------
    function shuffle(a) {
        for (var i = a.length - 1; i > 0; i--) {
            var j = Math.floor(Math.random() * (i + 1));
            var t = a[i]; a[i] = a[j]; a[j] = t;
        }
        return a;
    }

    function nextClip() {
        if (priority.length) return priority.shift();
        if (!pool.length) return null;

        if (!bag.length) {
            bag = shuffle(pool.map(function (_, i) { return i; }));
        }
        var idx = bag.pop();
        // Avoid replaying the clip that just played back-to-back.
        if (pool.length > 1 && pool[idx] && pool[idx].id === lastPlayedId) {
            if (!bag.length) bag = shuffle(pool.map(function (_, i) { return i; }));
            idx = bag.pop();
        }
        return pool[idx];
    }

    // ---------- data ----------
    function applyPool(newPool, isFirst) {
        newPool = Array.isArray(newPool) ? newPool.filter(function (c) { return c && c.id && c.src; }) : [];

        if (isFirst) {
            pool = newPool;
            pool.forEach(function (c) { knownIds.add(c.id); });
            priority = pool.slice(0, Math.min(2, pool.length)); // the two latest
            bag = [];
            return;
        }

        // Refresh: surface brand-new clips next (newest first, capped).
        var fresh = newPool.filter(function (c) { return !knownIds.has(c.id); });
        fresh.forEach(function (c) { knownIds.add(c.id); });
        pool = newPool;
        bag = [];
        if (fresh.length) priority = fresh.slice(0, 5).concat(priority);
    }

    function fetchClips(isFirst) {
        fetch("/api/obs/clips/" + encodeURIComponent(slug), { cache: "no-store" })
            .then(function (r) {
                if (!r.ok) throw new Error("HTTP " + r.status);
                return r.json();
            })
            .then(function (data) {
                applyPool(data.clips, isFirst);
                if (isFirst) {
                    if (pool.length || priority.length) playNext();
                    else showMsg("No clips found for " + escapeHtml(slug));
                }
            })
            .catch(function (e) {
                console.warn("clip list fetch failed:", e);
                if (isFirst) {
                    showMsg("Couldn't load clips — retrying…");
                    setTimeout(function () { fetchClips(true); }, 8000);
                }
            });
    }

    // ---------- playback ----------
    function playNext() {
        var clip = nextClip();
        if (!clip) { showMsg("No clips to display"); return; }
        lastPlayedId = clip.id;
        setOverlay(clip);
        armStallWatchdog();
        loadSource(clip.src);
    }

    function loadSource(src) {
        if (window.Hls && window.Hls.isSupported()) {
            if (hls) { try { hls.destroy(); } catch (e) {} hls = null; }
            hls = new Hls({ maxBufferLength: 30, maxMaxBufferLength: 60 });
            hls.on(Hls.Events.ERROR, function (_e, data) {
                if (data && data.fatal) { console.warn("HLS fatal:", data.type, data.details); skip(); }
            });
            hls.on(Hls.Events.MANIFEST_PARSED, tryPlay);
            hls.loadSource(src);
            hls.attachMedia(video);
        } else if (video.canPlayType("application/vnd.apple.mpegurl")) {
            video.src = src;       // native HLS (Safari)
            tryPlay();
        } else {
            skip();
        }
    }

    function tryPlay() {
        video.muted = wantMuted;
        var p = video.play();
        if (p && p.catch) {
            p.catch(function () {
                // Autoplay-with-sound blocked (normal browser preview) → mute & retry.
                video.muted = true;
                video.play().catch(function () { /* a click will start it */ });
            });
        }
    }

    function skip() {
        clearStallWatchdog();
        fails++;
        if (fails > 8) {
            showMsg("Clips unavailable — retrying…");
            setTimeout(function () { fails = 0; fetchClips(true); }, 15000);
            return;
        }
        setTimeout(playNext, 400);
    }

    function armStallWatchdog() {
        clearStallWatchdog();
        // If nothing is playing within 15s, give up on this clip.
        stallTimer = setTimeout(function () { console.warn("stall watchdog"); skip(); }, 15000);
    }
    function clearStallWatchdog() {
        if (stallTimer) { clearTimeout(stallTimer); stallTimer = null; }
    }

    video.addEventListener("ended", playNext);
    video.addEventListener("error", skip);
    video.addEventListener("playing", function () { fails = 0; clearStallWatchdog(); hideMsg(); });

    // ---------- UI ----------
    var overlayTimer = null;
    function setOverlay(clip) {
        if (!showOverlay) return;
        overlayEl.querySelector(".title").textContent = clip.title || "";
        var bits = [];
        if (clip.creator) bits.push(clip.creator);
        if (clip.category) bits.push(clip.category);
        overlayEl.querySelector(".meta").textContent = bits.join("  ·  ");
        overlayEl.classList.add("show");
        if (overlayTimer) clearTimeout(overlayTimer);
        overlayTimer = setTimeout(function () { overlayEl.classList.remove("show"); }, 6000);
    }
    function showMsg(t) { msgEl.textContent = t; msgEl.classList.remove("hidden"); }
    function hideMsg() { msgEl.classList.add("hidden"); }
    function escapeHtml(s) { return String(s).replace(/[&<>"]/g, function (c) {
        return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]; }); }

    // ---------- boot ----------
    if (!slug) {
        showMsg("No channel in URL. Use /obs/clips/{slug}");
        return;
    }
    fetchClips(true);
    setInterval(function () { fetchClips(false); }, Math.max(1, refreshMin) * 60 * 1000);
})();
