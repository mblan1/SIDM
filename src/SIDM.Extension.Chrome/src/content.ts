/**
 * Content script: injects a "Download via SIDM" button on supported video
 * sites. Strategy per host:
 *   - Primary: drop the button into the player's own control bar (next to
 *     YouTube's settings gear / fullscreen icon). This is the "true overlay
 *     on the video" the user asks for and survives all SPA navigations
 *     because YouTube re-uses the same #movie_player container.
 *   - Fallback: a floating button anchored to the top-right of the viewport,
 *     for hosts whose player chrome we don't have a hand-tuned selector for.
 *
 * No framework, no observer libraries. We poll once a second for up to 30 s
 * after each navigation — YouTube's player is built up over several frames
 * after the page-level navigation event fires.
 *
 * Console-friendly: every life-cycle moment logs with the "[SIDM]" prefix
 * so a user can verify the script is loading via DevTools when the button
 * doesn't appear.
 */

const BUTTON_ID = 'sidm-overlay-download';
const LOG = (...args: unknown[]) => console.log('[SIDM]', ...args);

LOG('content script loaded for', location.hostname);

/**
 * Per-host placement rules. The FIRST selector to resolve wins; "in-player"
 * placements (rendered inside the video chrome) are listed first because
 * that's where the user expects the button. Metadata-row placements (below
 * the video) come second as a graceful fallback if the player rules don't
 * resolve in time.
 */
interface PlacementRule {
    /** CSS selector for the container the button is appended to. */
    container: string;
    /** When set, button is inserted BEFORE this descendant of the container. */
    anchor?: string;
    /** When true, the button gets player-chrome styling (transparent bg,
     *  white text) instead of the default solid SIDM-blue pill. */
    inPlayer?: boolean;
}

const PLACEMENTS: Record<string, PlacementRule[]> = {
    'youtube.com': [
        // YouTube's right-side player controls: settings gear, subtitles,
        // theater mode, fullscreen. We slot in before the gear so SIDM
        // sits as the LEFT-most icon in that cluster.
        { container: '.ytp-right-controls', anchor: '.ytp-settings-button', inPlayer: true },
        // Last-ditch fallback inside the player area.
        { container: '#movie_player', inPlayer: true },
    ],
    'youtu.be': [
        { container: '.ytp-right-controls', anchor: '.ytp-settings-button', inPlayer: true },
        { container: '#movie_player', inPlayer: true },
    ],
    'vimeo.com': [
        { container: '.player .vp-controls' , inPlayer: true },
    ],
    // Twitch / TikTok / Dailymotion / Facebook / Instagram all vary too
    // much site-to-site for a hand-tuned selector; the floating-overlay
    // fallback covers them.
};

function makePlayerChromeButton(): HTMLButtonElement {
    // Mimic YouTube's other control buttons: transparent, white SVG.
    // We deliberately DO NOT add the `ytp-button` class — that class's
    // stylesheet ships `display:block` on the button and `width:100%
    // height:100%` on any child <svg>, both of which beat our inline
    // (non-!important) styles. The icon ended up 62x52 instead of 28x28
    // and aligned to the corner instead of centered. By omitting the
    // class and forcing our values with !important via cssText, we keep
    // total control of the layout.
    const btn = document.createElement('button');
    btn.id = BUTTON_ID;
    btn.type = 'button';
    btn.className = 'sidm-ytp-button';
    btn.title = 'Download this video with SIDM';
    btn.setAttribute('aria-label', 'Download with SIDM');
    btn.innerHTML = `
        <svg viewBox="0 0 36 36" aria-hidden="true"
             style="display:block !important; width:24px !important; height:24px !important; flex-shrink:0;">
            <path d="M 18 9 L 18 22 M 12 17 L 18 23 L 24 17 M 11 27 L 25 27"
                  stroke="white" stroke-width="2.4" fill="none"
                  stroke-linecap="round" stroke-linejoin="round" />
        </svg>`;
    // cssText with !important on every property — YouTube's stylesheet is
    // higher-specificity than a plain inline style, so we need the !important
    // bit to win.
    btn.style.cssText = `
        width: 38px !important;
        height: 36px !important;
        padding: 0 !important;
        margin: 0 !important;
        border: 0 !important;
        background: transparent !important;
        vertical-align: top !important;
        cursor: pointer !important;
        display: inline-flex !important;
        align-items: center !important;
        justify-content: center !important;
        opacity: 0.92;
    `;
    // Light hover affordance to match the other control buttons.
    btn.addEventListener('mouseenter', () => btn.style.setProperty('opacity', '1', 'important'));
    btn.addEventListener('mouseleave', () => btn.style.setProperty('opacity', '0.92', 'important'));
    btn.addEventListener('click', onClick);
    return btn;
}

function makePillButton(): HTMLButtonElement {
    const btn = document.createElement('button');
    btn.id = BUTTON_ID;
    btn.type = 'button';
    btn.title = 'Download this video with SIDM';
    btn.setAttribute('aria-label', 'Download with SIDM');
    btn.innerHTML = '<span class="sidm-dl-icon">⬇</span><span class="sidm-dl-label">SIDM</span>';
    Object.assign(btn.style, {
        display: 'inline-flex',
        alignItems: 'center',
        gap: '6px',
        padding: '6px 12px',
        marginRight: '8px',
        background: '#0067C0',
        color: 'white',
        border: 'none',
        borderRadius: '18px',
        font: '500 13px system-ui, sans-serif',
        cursor: 'pointer',
        zIndex: '9999',
    } as Partial<CSSStyleDeclaration>);
    btn.addEventListener('click', onClick);
    return btn;
}

function makeFloatingButton(): HTMLButtonElement {
    const btn = makePillButton();
    Object.assign(btn.style, {
        position: 'fixed',
        top: '16px',
        right: '16px',
        zIndex: '2147483647',
        boxShadow: '0 4px 12px rgba(0,0,0,0.4)',
    } as Partial<CSSStyleDeclaration>);
    return btn;
}

function onClick(e: Event) {
    e.preventDefault();
    e.stopPropagation();
    LOG('button clicked → asking background to open picker for', location.href);
    chrome.runtime.sendMessage(
        { type: 'sidm:open-picker', url: location.href },
        (resp) => {
            if (chrome.runtime.lastError) {
                console.warn('[SIDM] sendMessage failed:', chrome.runtime.lastError.message);
            } else {
                LOG('background ack:', resp);
            }
        },
    );
}

function hostKey(): string | null {
    const host = location.hostname.toLowerCase();
    for (const key of Object.keys(PLACEMENTS)) {
        if (host === key || host.endsWith('.' + key)) return key;
    }
    return null;
}

function tryPlace(): boolean {
    if (document.getElementById(BUTTON_ID)) return true;

    const key = hostKey();
    const rules = key ? PLACEMENTS[key] : [];

    for (const rule of rules) {
        try {
            const container = document.querySelector(rule.container);
            if (!container) continue;
            const btn = rule.inPlayer ? makePlayerChromeButton() : makePillButton();
            if (rule.anchor) {
                const anchor = container.querySelector(rule.anchor);
                // Insert into the anchor's actual parent — querySelector
                // finds descendants at any depth, but insertBefore requires
                // the reference node to be a direct child. YouTube wraps
                // the settings button inside its controls, so we'd throw
                // NotFoundError if we used `container.insertBefore` here.
                if (anchor && anchor.parentNode) {
                    anchor.parentNode.insertBefore(btn, anchor);
                    LOG('injected before', rule.anchor, 'inside', rule.container);
                    return true;
                }
            }
            container.prepend(btn);
            LOG('injected into', rule.container, '(prepended)');
            return true;
        } catch (err) {
            // One bad selector mustn't kill the polling loop — log and
            // fall through to the next rule.
            console.warn('[SIDM] placement rule failed:', rule, err);
        }
    }

    // Floating fallback only when a <video> is on the page. Keeps the button
    // off plain navigation pages (YouTube subscriptions feed, channel home).
    if (document.querySelector('video')) {
        try {
            document.body.appendChild(makeFloatingButton());
            LOG('injected floating fallback (no host placement matched)');
            return true;
        } catch (err) {
            console.warn('[SIDM] floating fallback failed:', err);
        }
    }
    return false;
}

let lastUrl = location.href;
let attempts = 0;
const MAX_ATTEMPTS = 30;

function tick() {
    if (location.href !== lastUrl) {
        lastUrl = location.href;
        attempts = 0;
        const existing = document.getElementById(BUTTON_ID);
        if (existing) {
            LOG('URL changed, removing stale button to re-inject at new player');
            existing.remove();
        }
    }
    if (tryPlace()) return;
    attempts++;
    if (attempts >= MAX_ATTEMPTS) {
        LOG(`gave up after ${MAX_ATTEMPTS} attempts; no matching DOM for ${location.hostname}`);
        return;
    }
    setTimeout(tick, 1000);
}

// Kick off twice: once now, once on the next frame in case the player just
// rendered. After that the 1 s polling loop in tick() takes over.
tick();
requestAnimationFrame(() => { if (!document.getElementById(BUTTON_ID)) tick(); });

// SPA navigation hook — YouTube uses history.pushState heavily.
const origPush = history.pushState;
history.pushState = function (...args) {
    const r = origPush.apply(this, args as Parameters<typeof history.pushState>);
    setTimeout(tick, 200);
    return r;
};
window.addEventListener('popstate', () => setTimeout(tick, 200));
