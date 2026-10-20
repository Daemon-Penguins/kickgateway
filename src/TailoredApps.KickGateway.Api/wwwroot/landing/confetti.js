// Confetti for the landing page's million-message milestones. Loaded as a JS module by Home.razor
// (IJSRuntime "import"); no dependencies, one full-screen canvas that removes itself when done.
// Two cannons at mid-height on the left and right edges fire inward and upward, then gravity takes over.

// The chart's categorical palette (dark surface steps) plus white, so it matches the rest of the page.
const COLORS = ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#9085e9', '#e66767', '#ffffff'];

/**
 * Fire a burst.
 * @param {number} pieces  number of confetti pieces (default 240)
 * @param {number} seconds how long the pieces stay on screen (default 4.5)
 */
export function burst(pieces = 240, seconds = 4.5) {
    if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

    const canvas = document.createElement('canvas');
    Object.assign(canvas.style, {
        position: 'fixed', inset: '0', width: '100%', height: '100%', pointerEvents: 'none', zIndex: '9999',
    });
    canvas.setAttribute('aria-hidden', 'true');
    document.body.appendChild(canvas);

    const ctx = canvas.getContext('2d');
    const dpr = window.devicePixelRatio || 1;
    const resize = () => {
        canvas.width = Math.floor(window.innerWidth * dpr);
        canvas.height = Math.floor(window.innerHeight * dpr);
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    };
    resize();
    window.addEventListener('resize', resize);

    // Three cannons: left edge, right edge (both aim inward) and bottom centre (straight up, wide spread).
    const parts = [];
    for (let i = 0; i < pieces; i++) {
        const cannon = i % 3;
        const side = cannon === 0 ? 1 : cannon === 1 ? -1 : 0;
        parts.push({
            x: cannon === 0 ? -12 : cannon === 1 ? window.innerWidth + 12 : window.innerWidth / 2 + (Math.random() - 0.5) * 120,
            y: cannon === 2 ? window.innerHeight + 12 : window.innerHeight * (0.35 + Math.random() * 0.35),
            vx: side !== 0 ? side * (9 + Math.random() * 14) : (Math.random() - 0.5) * 18,
            vy: cannon === 2 ? -(16 + Math.random() * 10) : -(9 + Math.random() * 11),
            w: 6 + Math.random() * 6,
            h: 4 + Math.random() * 6,
            rot: Math.random() * Math.PI * 2,
            vr: (Math.random() - 0.5) * 0.35,
            wobble: Math.random() * Math.PI * 2,
            color: COLORS[i % COLORS.length],
        });
    }

    const start = performance.now();
    const frame = (t) => {
        const elapsed = (t - start) / 1000;
        ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
        const alpha = Math.max(0, Math.min(1, seconds - elapsed)); // fade through the last second
        for (const p of parts) {
            p.vy += 0.3;
            p.vx *= 0.985;
            p.x += p.vx;
            p.y += p.vy;
            p.rot += p.vr;
            p.wobble += 0.12;
            ctx.save();
            ctx.globalAlpha = alpha;
            ctx.translate(p.x + Math.sin(p.wobble) * 2, p.y);
            ctx.rotate(p.rot);
            ctx.fillStyle = p.color;
            ctx.fillRect(-p.w / 2, -p.h / 2, p.w, p.h);
            ctx.restore();
        }
        if (elapsed < seconds) {
            requestAnimationFrame(frame);
        } else {
            window.removeEventListener('resize', resize);
            canvas.remove();
        }
    };
    requestAnimationFrame(frame);
}
