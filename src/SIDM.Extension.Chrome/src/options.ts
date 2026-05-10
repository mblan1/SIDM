import { type HelloResponse, type IpcMessage, NATIVE_HOST_ID } from './ipc';

interface SidmSettings {
    captureEnabled: boolean;
    minSizeBytes: number;
    bypassHosts: string[];
}

const DEFAULTS: SidmSettings = {
    captureEnabled: true,
    minSizeBytes: 0,
    bypassHosts: [],
};

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

const captureCheckbox = $<HTMLInputElement>('capture-enabled');
const minSizeInput = $<HTMLInputElement>('min-size');
const bypassTextarea = $<HTMLTextAreaElement>('bypass-hosts');
const saveBtn = $<HTMLButtonElement>('save-btn');
const saveFeedback = $<HTMLSpanElement>('save-feedback');
const recheckBtn = $<HTMLButtonElement>('recheck-btn');
const statusCard = document.querySelector('section.status-card') as HTMLElement;
const statusText = $<HTMLSpanElement>('status-text');
const statusDetail = $<HTMLParagraphElement>('status-detail');
const versionLabel = $<HTMLSpanElement>('version');

async function load(): Promise<void> {
    const stored = await chrome.storage.local.get(DEFAULTS);
    const settings = { ...DEFAULTS, ...stored } as SidmSettings;

    captureCheckbox.checked = settings.captureEnabled;
    minSizeInput.value = String(settings.minSizeBytes);
    bypassTextarea.value = settings.bypassHosts.join('\n');

    versionLabel.textContent = `Extension v${chrome.runtime.getManifest().version}`;
}

async function save(): Promise<void> {
    const settings: SidmSettings = {
        captureEnabled: captureCheckbox.checked,
        minSizeBytes: Math.max(0, parseInt(minSizeInput.value, 10) || 0),
        bypassHosts: bypassTextarea.value
            .split('\n')
            .map(s => s.trim())
            .filter(s => s.length > 0),
    };
    await chrome.storage.local.set(settings);
    flash('Saved.');
}

function flash(text: string): void {
    saveFeedback.textContent = text;
    saveFeedback.classList.remove('fading');
    setTimeout(() => saveFeedback.classList.add('fading'), 1200);
    setTimeout(() => { saveFeedback.textContent = ''; }, 1800);
}

function setStatus(state: 'ok' | 'bad' | 'warn', text: string, detail = ''): void {
    statusCard.classList.remove('ok', 'bad');
    if (state === 'ok') statusCard.classList.add('ok');
    if (state === 'bad') statusCard.classList.add('bad');
    statusText.textContent = text;
    statusDetail.textContent = detail;
}

function checkConnection(): void {
    setStatus('warn', 'Checking…');
    let port: chrome.runtime.Port;
    try {
        port = chrome.runtime.connectNative(NATIVE_HOST_ID);
    } catch (e) {
        setStatus('bad', 'Native host not registered', String(e));
        return;
    }

    let answered = false;

    const timeout = setTimeout(() => {
        if (answered) return;
        port.disconnect();
        setStatus('bad', 'No response from SIDM',
            'Is SIDM running, and did you run `SIDM.App --register-hosts --extension-id <this extension id>`?');
    }, 4000);

    port.onMessage.addListener((msg: IpcMessage) => {
        if (msg.type === 'hello-response') {
            answered = true;
            clearTimeout(timeout);
            const hr = msg as HelloResponse;
            setStatus('ok', `Connected to ${hr.appName}`,
                `version ${hr.appVersion} — capabilities: ${hr.capabilities.join(', ')}`);
            port.disconnect();
        }
    });

    port.onDisconnect.addListener(() => {
        if (answered) return;
        clearTimeout(timeout);
        const err = chrome.runtime.lastError?.message ?? 'unknown';
        setStatus('bad', 'Native host failed to launch', err);
    });

    port.postMessage({
        type: 'hello',
        clientName: 'SIDM-Chrome-Extension-Options',
        clientVersion: chrome.runtime.getManifest().version,
    });
}

saveBtn.addEventListener('click', save);
recheckBtn.addEventListener('click', checkConnection);
load().then(checkConnection);
