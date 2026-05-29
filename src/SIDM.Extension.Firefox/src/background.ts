/**
 * SIDM extension service worker.
 *
 * Four jobs:
 *   1. Maintain a Native Messaging port to com.sidm.host.
 *   2. Intercept browser downloads and forward them to SIDM with full context
 *      (cookies, referer, UA) so SIDM can replay the request authentically.
 *   3. Provide a "Download with SIDM" context menu for links / videos / audio.
 *   4. Sniff HLS / DASH manifests on every tab so the popup can offer
 *      one-click capture of in-page video players (Phase 4.D).
 */

import {
    type DownloadRequest,
    type DownloadResponse,
    type ErrorMessage,
    type IpcMessage,
    type ListFormatsResponse,
    NATIVE_HOST_ID,
} from './ipc';
import { installSniffer } from './sniffer';

const CLIENT_NAME = 'SIDM-Chrome-Extension';
const CLIENT_VERSION = chrome.runtime.getManifest().version;

/**
 * Returns true when SIDM bundles a strictly newer extension than the one
 * currently loaded. Plain dotted-int compare ("0.1.16" < "0.1.17";
 * "0.2.0" > "0.1.99"). Pre-release suffixes fall back to ordinal compare
 * on the leftover — close enough for the upgrade signal.
 */
function isExtensionOutdated(installed: string, bundled: string): boolean {
    if (!installed || !bundled || installed === bundled) return false;
    const splitNums = (v: string): number[] =>
        v.split(/[^0-9]+/).filter(s => s.length > 0).map(s => parseInt(s, 10));
    const a = splitNums(installed);
    const b = splitNums(bundled);
    const len = Math.max(a.length, b.length);
    for (let i = 0; i < len; i++) {
        const x = a[i] ?? 0;
        const y = b[i] ?? 0;
        if (x !== y) return x < y;
    }
    return installed < bundled;
}

interface SidmSettings {
    captureEnabled: boolean;
    minSizeBytes: number;
    bypassHosts: string[];
}

const DEFAULT_SETTINGS: SidmSettings = {
    captureEnabled: true,
    minSizeBytes: 0,
    bypassHosts: [],
};

let port: chrome.runtime.Port | null = null;

/**
 * Promises waiting for a `list-formats-response` from the native host. Keyed
 * by the URL we asked about so concurrent picker windows don't clobber each
 * other's replies. Errors get pushed in the second slot — when the native
 * host returns an `error` between request and response, we resolve all
 * outstanding picker promises with the error so spinners stop.
 */
const pendingFormatLookups = new Map<string, {
    resolve: (resp: ListFormatsResponse) => void;
    reject: (err: ErrorMessage) => void;
}>();

function ensurePort(): chrome.runtime.Port {
    if (port) return port;

    port = chrome.runtime.connectNative(NATIVE_HOST_ID);

    port.onMessage.addListener((msg: IpcMessage) => {
        if (msg.type === 'hello-response') {
            console.log('[SIDM] Connected to', msg.appName, msg.appVersion);
            // Self-reload when SIDM bundles a newer extension than the
            // one Chrome currently has loaded in memory. SIDM's
            // auto-extract on startup has already overwritten the
            // unpacked-extension folder with the new files; reloading
            // makes Chrome pick them up without the user having to
            // visit chrome://extensions/.
            if (msg.bundledExtensionVersion &&
                isExtensionOutdated(CLIENT_VERSION, msg.bundledExtensionVersion)) {
                console.log(
                    `[SIDM] Self-reload: installed v${CLIENT_VERSION} < bundled v${msg.bundledExtensionVersion}`);
                chrome.runtime.reload();
                return;
            }
        } else if (msg.type === 'download-response') {
            handleDownloadAck(msg);
        } else if (msg.type === 'list-formats-response') {
            const pending = pendingFormatLookups.get(msg.url);
            if (pending) {
                pendingFormatLookups.delete(msg.url);
                pending.resolve(msg);
            }
        } else if (msg.type === 'error') {
            // An error after a list-formats request usually IS the list-formats
            // reply — fail every pending picker so the user sees the message
            // rather than a forever-spinner.
            if (pendingFormatLookups.size > 0) {
                for (const [, p] of pendingFormatLookups) p.reject(msg);
                pendingFormatLookups.clear();
            }
            handleHostError(msg);
        }
    });

    port.onDisconnect.addListener(() => {
        const reason = chrome.runtime.lastError?.message ?? 'unknown';
        console.warn('[SIDM] Native host disconnected:', reason);
        port = null;
    });

    // Handshake — server responds with capabilities; tells us we're talking
    // to a SIDM that's compatible with this extension.
    port.postMessage({
        type: 'hello',
        clientName: CLIENT_NAME,
        clientVersion: CLIENT_VERSION,
    });

    return port;
}

async function getSettings(): Promise<SidmSettings> {
    const stored = await chrome.storage.local.get(DEFAULT_SETTINGS);
    return { ...DEFAULT_SETTINGS, ...stored } as SidmSettings;
}

function isBypassed(url: string, bypassHosts: string[]): boolean {
    if (bypassHosts.length === 0) return false;
    try {
        const host = new URL(url).hostname;
        return bypassHosts.some(pattern => host === pattern || host.endsWith('.' + pattern));
    } catch {
        return false;
    }
}

async function collectCookies(url: string): Promise<Record<string, string>> {
    try {
        const cookies = await chrome.cookies.getAll({ url });
        const map: Record<string, string> = {};
        for (const c of cookies) map[c.name] = c.value;
        return map;
    } catch (e) {
        console.warn('[SIDM] Failed to read cookies:', e);
        return {};
    }
}

function describe(item: chrome.downloads.DownloadItem): string {
    return `${item.filename || '(unnamed)'} (${item.fileSize > 0 ? item.fileSize + ' bytes' : 'size unknown'})`;
}

async function captureDownload(item: chrome.downloads.DownloadItem): Promise<void> {
    const settings = await getSettings();
    if (!settings.captureEnabled) return;

    const url = item.finalUrl || item.url;
    if (!url || !/^https?:/i.test(url)) return;
    if (isBypassed(url, settings.bypassHosts)) {
        console.log('[SIDM] Bypassed by user setting:', url);
        return;
    }
    if (settings.minSizeBytes > 0 && item.fileSize > 0 && item.fileSize < settings.minSizeBytes) {
        return;
    }

    // Cancel the browser's own download — SIDM is taking over.
    try {
        await chrome.downloads.cancel(item.id);
        await chrome.downloads.erase({ id: item.id });
    } catch (e) {
        console.warn('[SIDM] Could not cancel browser download:', e);
    }

    const cookies = await collectCookies(url);
    const headers: Record<string, string> = {};
    if (item.referrer) headers['Referer'] = item.referrer;

    const fileName = item.filename
        ? item.filename.split(/[\\/]/).pop() ?? undefined
        : undefined;

    const request: DownloadRequest = {
        type: 'download',
        url,
        fileName,
        headers: Object.keys(headers).length > 0 ? headers : undefined,
        cookies: Object.keys(cookies).length > 0 ? cookies : undefined,
        referer: item.referrer || undefined,
        userAgent: navigator.userAgent,
        expectedLength: item.fileSize > 0 ? item.fileSize : undefined,
        mime: item.mime || undefined,
    };

    try {
        ensurePort().postMessage(request);
        console.log('[SIDM] Forwarded:', describe(item));
    } catch (e) {
        notifyError('Could not reach SIDM', String(e));
    }
}

function handleDownloadAck(msg: DownloadResponse): void {
    chrome.notifications.create({
        type: 'basic',
        iconUrl: 'data:image/svg+xml;base64,' + btoa(MINIMAL_ICON_SVG),
        title: 'SIDM',
        message: `Queued (id ${msg.downloadId}). ${msg.status}`,
    });
}

function handleHostError(msg: ErrorMessage): void {
    notifyError(msg.reason, msg.detail ?? '');
}

function notifyError(title: string, body: string): void {
    chrome.notifications.create({
        type: 'basic',
        iconUrl: 'data:image/svg+xml;base64,' + btoa(MINIMAL_ICON_SVG),
        title: `SIDM: ${title}`,
        message: body || ' ',
    });
}

const MINIMAL_ICON_SVG = `<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48"><rect width="48" height="48" rx="8" fill="#0078D4"/><path d="M24 12 L24 30 M16 24 L24 32 L32 24 M14 36 L34 36" stroke="white" stroke-width="3" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg>`;

/**
 * Opens the format-picker popup window for a URL. Shared by the content-script
 * overlay button and the right-click context menu, so any site yt-dlp supports
 * can be downloaded with a format choice — not just the hand-tuned hosts.
 */
function openPickerWindow(url: string): Promise<chrome.windows.Window> {
    const target = chrome.runtime.getURL('picker.html') + '?url=' + encodeURIComponent(url);
    return chrome.windows.create({ url: target, type: 'popup', width: 480, height: 600 });
}

// --- Wiring ---

chrome.runtime.onInstalled.addListener(() => {
    chrome.storage.local.get(DEFAULT_SETTINGS, (existing) => {
        chrome.storage.local.set({ ...DEFAULT_SETTINGS, ...existing });
    });

    chrome.contextMenus.removeAll(() => {
        // Direct capture — for a concrete file/media URL behind a link or an
        // <audio>/<video> src. Sends the URL straight to SIDM.
        chrome.contextMenus.create({
            id: 'sidm-download-link',
            title: 'Download with SIDM',
            contexts: ['link', 'video', 'audio'],
        });
        // Format picker — works on the current PAGE (any yt-dlp-supported
        // site, not just YouTube). Probes the page URL and lets the user pick
        // a video resolution or audio-only track. Always available via
        // right-click anywhere on the page.
        chrome.contextMenus.create({
            id: 'sidm-pick-format',
            title: 'Download video / audio with SIDM…',
            contexts: ['page', 'video', 'audio', 'frame', 'selection', 'link'],
        });
    });
});

chrome.downloads.onCreated.addListener((item) => {
    // onCreated fires before the download starts. We capture here so we can
    // cancel before the browser writes any bytes.
    captureDownload(item).catch(e => console.error('[SIDM] capture failed:', e));
});

chrome.contextMenus.onClicked.addListener(async (info) => {
    // Format-picker entry: open the picker for the most relevant URL. For a
    // right-click directly on media, prefer its http(s) src; otherwise (and
    // for the YouTube case where srcUrl is a blob: stream) use the page URL,
    // which is what yt-dlp actually needs.
    if (info.menuItemId === 'sidm-pick-format') {
        const src = info.srcUrl;
        const usableSrc = src && /^https?:/i.test(src) ? src : undefined;
        const url = usableSrc ?? info.linkUrl ?? info.frameUrl ?? info.pageUrl;
        if (!url || !/^https?:/i.test(url)) {
            notifyError('Nothing to download here', 'Open the page with the video, then try again.');
            return;
        }
        try {
            await openPickerWindow(url);
        } catch (e) {
            notifyError('Could not open the format picker', String(e));
        }
        return;
    }

    if (info.menuItemId !== 'sidm-download-link') return;
    const url = info.linkUrl || info.srcUrl;
    if (!url) return;

    const cookies = await collectCookies(url);
    const headers: Record<string, string> = {};
    if (info.pageUrl) headers['Referer'] = info.pageUrl;

    const request: DownloadRequest = {
        type: 'download',
        url,
        headers: Object.keys(headers).length > 0 ? headers : undefined,
        cookies: Object.keys(cookies).length > 0 ? cookies : undefined,
        referer: info.pageUrl,
        userAgent: navigator.userAgent,
    };

    try {
        ensurePort().postMessage(request);
    } catch (e) {
        notifyError('Could not reach SIDM', String(e));
    }
});

// Streaming-manifest sniffer (Phase 4.D). Runs once when the service worker
// boots; MV3 may evict and respawn the worker any time, but listeners
// registered at top level get reattached on every wake-up.
installSniffer();

// Popup → background channel. The popup parks the assembled DownloadRequest
// (with cookies pre-resolved) here and we forward it through the existing
// native messaging port. Keeping the post split this way lets the popup
// stay alive only long enough to gather context, then close.
chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    // Plain capture (existing).
    if (message?.type === 'sidm:capture-manifest') {
        const request = message.request as DownloadRequest;
        try {
            ensurePort().postMessage(request);
            sendResponse({ ok: true });
        } catch (e) {
            sendResponse({ ok: false, error: String(e) });
        }
        return true;
    }

    // Content-script overlay → "open the format-picker for this URL".
    // Opens picker.html as a small popup window; the picker UI then asks
    // back for the format list via 'sidm:list-formats'.
    if (message?.type === 'sidm:open-picker') {
        const url = String(message.url ?? '');
        if (!url) { sendResponse({ ok: false, error: 'no url' }); return true; }
        openPickerWindow(url).then(
            () => sendResponse({ ok: true }),
            (e) => sendResponse({ ok: false, error: String(e) }));
        return true;
    }

    // Picker → "probe formats". Replies asynchronously with either a
    // ListFormatsResponse or an ErrorMessage. We correlate by URL.
    if (message?.type === 'sidm:list-formats') {
        const url = String(message.url ?? '');
        if (!url) { sendResponse({ ok: false, error: 'no url' }); return true; }

        // De-dupe: if another picker is already waiting on this URL, piggy-back.
        if (pendingFormatLookups.has(url)) {
            sendResponse({ ok: false, error: 'A probe for this URL is already in flight.' });
            return true;
        }

        new Promise<ListFormatsResponse>((resolve, reject) => {
            pendingFormatLookups.set(url, { resolve, reject });
            try {
                ensurePort().postMessage({ type: 'list-formats', url });
            } catch (e) {
                pendingFormatLookups.delete(url);
                reject({ type: 'error', reason: 'NoNativeHost', detail: String(e) });
            }
        }).then(
            (resp) => sendResponse({ ok: true, response: resp }),
            (err) => sendResponse({ ok: false, error: err.reason ?? 'failed', detail: err.detail }),
        );
        return true;
    }

    return false;
});

// (No chrome.action.onClicked listener — the manifest declares default_popup
// instead, so a click opens popup.html. The popup has a settings button
// that calls openOptionsPage().)
