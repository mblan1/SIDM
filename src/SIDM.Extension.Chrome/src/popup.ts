/**
 * SIDM popup.
 *
 * Three jobs:
 *   1. Show whether the SIDM desktop app is reachable over Native Messaging,
 *      plus its version (probed with the same hello handshake the options page
 *      uses — a throwaway port, so the popup stays self-contained).
 *   2. A master enable/disable switch bound to `captureEnabled`. When off, the
 *      browser handles downloads normally and SIDM intercepts nothing.
 *   3. Still surface any HLS/DASH manifests the sniffer parked on the tab so
 *      in-page video players remain one-click captures.
 */

import { type DownloadRequest, type HelloResponse, type IpcMessage, NATIVE_HOST_ID } from './ipc';
import { listForTab, type SniffedManifest } from './sniffer';
import { isExtensionOutdated } from './version';

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

const settingsBtn = $<HTMLButtonElement>('settings-btn');
const recheckBtn = $<HTMLButtonElement>('recheck-btn');
const statusCard = $<HTMLElement>('status-card');
const connText = $<HTMLSpanElement>('conn-text');
const connDetail = $<HTMLSpanElement>('conn-detail');
const captureToggle = $<HTMLInputElement>('capture-toggle');
const toggleHint = $<HTMLSpanElement>('toggle-hint');
const listSection = $<HTMLElement>('list-section');
const emptySection = $<HTMLElement>('empty');
const manifestList = $<HTMLUListElement>('manifests');
const versionEl = $<HTMLSpanElement>('version');
const statusEl = $<HTMLSpanElement>('status');
const updateBanner = $<HTMLElement>('update-banner');
const updateText = $<HTMLSpanElement>('update-text');
const reloadBtn = $<HTMLButtonElement>('reload-btn');

const EXT_VERSION = chrome.runtime.getManifest().version;
versionEl.textContent = `Extension v${EXT_VERSION}`;

settingsBtn.addEventListener('click', () => {
    chrome.runtime.openOptionsPage();
    window.close();
});
recheckBtn.addEventListener('click', checkConnection);

// Apply a newer extension that SIDM has already extracted to disk. A plain
// chrome.runtime.reload() restarts the extension so Chrome reloads those files
// — no chrome://extensions visit needed. The reload tears down this popup.
reloadBtn.addEventListener('click', () => {
    reloadBtn.disabled = true;
    reloadBtn.textContent = 'Reloading…';
    chrome.runtime.reload();
});

/** Reveals the "update available" banner when the app bundles a newer build. */
function maybeOfferUpdate(bundledVersion: string | undefined): void {
    if (bundledVersion && isExtensionOutdated(EXT_VERSION, bundledVersion)) {
        updateText.textContent = `Update available: v${bundledVersion}`;
        updateBanner.hidden = false;
    } else {
        updateBanner.hidden = true;
    }
}

// --- Master enable / disable ---------------------------------------------

function reflectToggle(enabled: boolean): void {
    captureToggle.checked = enabled;
    toggleHint.textContent = enabled
        ? 'Browser downloads are handed to SIDM.'
        : 'Off — downloads use your browser as normal.';
}

async function loadToggle(): Promise<void> {
    // captureEnabled defaults to true (matches the background's DEFAULT_SETTINGS).
    const { captureEnabled } = await chrome.storage.local.get({ captureEnabled: true });
    reflectToggle(captureEnabled !== false);
}

captureToggle.addEventListener('change', async () => {
    const enabled = captureToggle.checked;
    // The background mirrors chrome.storage.local into its hot-path cache via a
    // storage.onChanged listener, so this is all that's needed to flip capture.
    await chrome.storage.local.set({ captureEnabled: enabled });
    reflectToggle(enabled);
});

// --- Connection status ----------------------------------------------------

function setConn(state: 'ok' | 'bad' | 'checking', text: string, detail = ''): void {
    statusCard.classList.remove('ok', 'bad', 'checking');
    statusCard.classList.add(state);
    connText.textContent = text;
    connDetail.textContent = detail;
}

/**
 * Opens a throwaway native port, says hello, and reports the result. Mirrors
 * the options page's check so the two stay consistent. A 4 s timeout covers the
 * "host registered but app not running" case where the port connects but never
 * answers.
 */
function checkConnection(): void {
    setConn('checking', 'Checking connection…');

    let port: chrome.runtime.Port;
    try {
        port = chrome.runtime.connectNative(NATIVE_HOST_ID);
    } catch (e) {
        setConn('bad', 'SIDM not reachable', String(e));
        return;
    }

    let answered = false;
    const timeout = setTimeout(() => {
        if (answered) return;
        port.disconnect();
        setConn('bad', 'SIDM not running', 'Start the SIDM desktop app, then re-check.');
    }, 4000);

    port.onMessage.addListener((msg: IpcMessage) => {
        if (msg.type !== 'hello-response') return;
        answered = true;
        clearTimeout(timeout);
        const hr = msg as HelloResponse;
        const caps = hr.capabilities?.length ? hr.capabilities.join(', ') : '—';
        setConn('ok', `Connected to ${hr.appName}`, `App v${hr.appVersion} · ${caps}`);
        maybeOfferUpdate(hr.bundledExtensionVersion);
        port.disconnect();
    });

    port.onDisconnect.addListener(() => {
        if (answered) return;
        clearTimeout(timeout);
        const err = chrome.runtime.lastError?.message ?? 'unknown';
        setConn('bad', 'SIDM not reachable', err);
    });

    port.postMessage({
        type: 'hello',
        clientName: 'SIDM-Chrome-Extension-Popup',
        clientVersion: EXT_VERSION,
    });
}

// --- Video stream sniffer (secondary) ------------------------------------

async function loadManifests(): Promise<void> {
    const [activeTab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!activeTab?.id) {
        showStreams([], '');
        return;
    }
    const manifests = await listForTab(activeTab.id);
    showStreams(manifests, activeTab.url ?? '');
}

function showStreams(manifests: SniffedManifest[], pageUrl: string): void {
    const hasStreams = manifests.length > 0;
    listSection.hidden = !hasStreams;
    emptySection.hidden = hasStreams;
    if (!hasStreams) return;

    manifestList.innerHTML = '';
    for (const m of manifests) {
        manifestList.appendChild(renderItem(m, pageUrl));
    }
}

function renderItem(manifest: SniffedManifest, pageUrl: string): HTMLLIElement {
    const li = document.createElement('li');

    const badge = document.createElement('span');
    badge.className = `kind-badge kind-${manifest.kind}`;
    badge.textContent = manifest.kind;

    const url = document.createElement('span');
    url.className = 'manifest-url';
    url.textContent = manifest.url;
    url.title = manifest.url;

    li.appendChild(badge);
    li.appendChild(url);
    li.addEventListener('click', () => capture(manifest, pageUrl));
    return li;
}

async function capture(manifest: SniffedManifest, pageUrl: string): Promise<void> {
    // An explicit stream capture is a deliberate action, so it runs regardless
    // of the master toggle (which only gates automatic browser-download
    // interception).
    statusEl.textContent = 'Sending to SIDM…';

    const referer = manifest.pageUrl || pageUrl;
    let cookies: Record<string, string> | undefined;
    try {
        const all = await chrome.cookies.getAll({ url: manifest.url });
        if (all.length > 0) {
            cookies = {};
            for (const c of all) cookies[c.name] = c.value;
        }
    } catch {
        // If cookies fail we still send the request — the server may not need them.
    }

    const request: DownloadRequest = {
        type: 'download',
        url: manifest.url,
        referer: referer || undefined,
        userAgent: navigator.userAgent,
        cookies,
    };

    chrome.runtime.sendMessage({ type: 'sidm:capture-manifest', request }, (reply) => {
        const err = chrome.runtime.lastError?.message;
        if (err) {
            statusEl.textContent = `Failed: ${err}`;
            return;
        }
        statusEl.textContent = reply?.ok ? 'Sent. Check SIDM.' : (reply?.error ?? 'Unknown error');
        if (reply?.ok) setTimeout(() => window.close(), 500);
    });
}

// --- Boot -----------------------------------------------------------------

void loadToggle();
checkConnection();
void loadManifests();
