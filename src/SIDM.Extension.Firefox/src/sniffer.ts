/**
 * Streaming-manifest sniffer. Listens for HLS / DASH responses observed by
 * any frame in any tab and parks the URL (plus referer + cookies later) in
 * a per-tab map so the popup can offer one-click capture.
 *
 * Phase 4.D scope:
 *   - HLS:    Content-Type application/vnd.apple.mpegurl or application/x-mpegurl
 *             OR URL path ends in .m3u8 / .m3u
 *   - DASH:   Content-Type application/dash+xml
 *             OR URL path ends in .mpd
 *
 * MV3 service workers can be evicted at any time, so detected state lives
 * in chrome.storage.session — the popup re-reads it on open and the
 * background re-hydrates lazily when needed. The session store is cleared
 * on browser restart, which matches user expectation ("manifests visited
 * earlier today, not last week").
 */

export type SniffKind = 'hls' | 'dash';

export interface SniffedManifest {
    kind: SniffKind;
    url: string;
    /** URL of the page that triggered the request (best effort, may be empty). */
    pageUrl: string;
    /** Browser-side timestamp; useful for ordering in the popup. */
    capturedAt: number;
}

const STORAGE_KEY = 'sidm.sniffed.byTab';
const MAX_PER_TAB = 16;

const HLS_CONTENT_TYPES = new Set([
    'application/vnd.apple.mpegurl',
    'application/x-mpegurl',
    'audio/mpegurl',
    'vnd.apple.mpegurl',
]);
const DASH_CONTENT_TYPES = new Set(['application/dash+xml']);

/**
 * Decides whether the response we just saw is a streaming manifest, and if
 * so which flavor. Returns null for non-matches. Pure function — exported
 * for testing in isolation if a future Jest config lands.
 */
export function classify(url: string, contentType: string | undefined): SniffKind | null {
    const ct = (contentType ?? '').toLowerCase().split(';')[0].trim();
    if (HLS_CONTENT_TYPES.has(ct)) return 'hls';
    if (DASH_CONTENT_TYPES.has(ct)) return 'dash';

    try {
        const path = new URL(url).pathname.toLowerCase();
        if (path.endsWith('.m3u8') || path.endsWith('.m3u')) return 'hls';
        if (path.endsWith('.mpd')) return 'dash';
    } catch {
        // Not a parseable URL; ignore.
    }
    return null;
}

/** Reads the per-tab manifest map from chrome.storage.session. */
async function readState(): Promise<Record<string, SniffedManifest[]>> {
    const stored = await chrome.storage.session.get(STORAGE_KEY);
    return (stored[STORAGE_KEY] as Record<string, SniffedManifest[]>) ?? {};
}

async function writeState(state: Record<string, SniffedManifest[]>): Promise<void> {
    await chrome.storage.session.set({ [STORAGE_KEY]: state });
}

/** Adds a manifest to the given tab's list and updates the action badge. */
async function record(tabId: number, manifest: SniffedManifest): Promise<void> {
    const state = await readState();
    const list = state[tabId] ?? [];

    // Dedupe by URL.
    if (list.some(m => m.url === manifest.url)) return;

    list.unshift(manifest);
    if (list.length > MAX_PER_TAB) list.length = MAX_PER_TAB;
    state[tabId] = list;
    await writeState(state);
    await updateBadge(tabId, list.length);
}

async function clearTab(tabId: number): Promise<void> {
    const state = await readState();
    if (!(tabId in state)) return;
    delete state[tabId];
    await writeState(state);
    await updateBadge(tabId, 0);
}

async function updateBadge(tabId: number, count: number): Promise<void> {
    try {
        await chrome.action.setBadgeBackgroundColor({ tabId, color: '#0078D4' });
        await chrome.action.setBadgeText({ tabId, text: count > 0 ? String(count) : '' });
    } catch {
        // Tab may have closed between detection and the badge call — ignore.
    }
}

/** Public: list of manifests currently known for the given tab. */
export async function listForTab(tabId: number): Promise<SniffedManifest[]> {
    const state = await readState();
    return state[tabId] ?? [];
}

/** Registers the webRequest + tab listeners. Call once at service-worker startup. */
export function installSniffer(): void {
    if (!chrome.webRequest) {
        console.warn('[SIDM] chrome.webRequest unavailable; sniffer disabled');
        return;
    }

    chrome.webRequest.onCompleted.addListener(
        (details) => {
            // tabId is -1 for requests from the extension itself or service
            // workers; we have no UI to surface them on, so ignore.
            if (details.tabId < 0) return;

            const contentType = details.responseHeaders
                ?.find(h => h.name.toLowerCase() === 'content-type')?.value;
            const kind = classify(details.url, contentType);
            if (!kind) return;

            // `initiator` is the only origin Chrome exposes here (scheme+host,
            // no path); Firefox additionally provides `documentUrl` (the full
            // document URL). The latter isn't in @types/chrome, so read it
            // defensively — undefined on Chrome, a real fallback on Firefox.
            const documentUrl = (details as { documentUrl?: string }).documentUrl;
            const manifest: SniffedManifest = {
                kind,
                url: details.url,
                pageUrl: details.initiator ?? documentUrl ?? '',
                capturedAt: Date.now(),
            };
            record(details.tabId, manifest).catch(e =>
                console.error('[SIDM] sniffer record failed:', e));
        },
        { urls: ['<all_urls>'] },
        ['responseHeaders'],
    );

    // Clear a tab's manifests when it navigates to a new page so the badge
    // count stays meaningful. Only clear on top-frame navigation (frameId 0).
    chrome.webNavigation?.onBeforeNavigate.addListener((details) => {
        if (details.frameId !== 0) return;
        clearTab(details.tabId).catch(e => console.error('[SIDM] sniffer clear failed:', e));
    });

    chrome.tabs.onRemoved.addListener((tabId) => {
        clearTab(tabId).catch(e => console.error('[SIDM] sniffer cleanup failed:', e));
    });
}
