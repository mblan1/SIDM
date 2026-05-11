/**
 * SIDM popup — shows streaming manifests the sniffer parked on the current
 * tab. Clicking one sends a download request to the background service
 * worker, which forwards it through the existing native messaging port.
 */

import type { DownloadRequest } from './ipc';
import { listForTab, type SniffedManifest } from './sniffer';

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

const emptySection = $<HTMLElement>('empty');
const listSection = $<HTMLElement>('list-section');
const manifestList = $<HTMLUListElement>('manifests');
const settingsBtn = $<HTMLButtonElement>('settings-btn');
const statusEl = $<HTMLSpanElement>('status');

settingsBtn.addEventListener('click', () => {
    chrome.runtime.openOptionsPage();
    window.close();
});

async function load(): Promise<void> {
    const [activeTab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!activeTab?.id) {
        setEmpty();
        return;
    }

    const manifests = await listForTab(activeTab.id);
    if (manifests.length === 0) {
        setEmpty();
        return;
    }

    listSection.hidden = false;
    emptySection.hidden = true;
    manifestList.innerHTML = '';
    for (const m of manifests) {
        manifestList.appendChild(renderItem(m, activeTab.url ?? ''));
    }
}

function setEmpty(): void {
    listSection.hidden = true;
    emptySection.hidden = false;
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

load();
