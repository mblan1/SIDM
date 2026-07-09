/**
 * Picker UI: opens with ?url=<encoded video URL>, asks the background to
 * run `list-formats`, renders rows grouped by Video / Audio, and on click
 * dispatches a `download` IPC message (the existing 'sidm:capture-manifest'
 * path in background.ts) carrying the chosen formatId + a pretty label.
 */

import type {
    DownloadRequest,
    FormatOption,
    ListFormatsResponse,
} from './ipc';

const params = new URLSearchParams(location.search);
const videoUrl = params.get('url') ?? '';

const $loading = document.getElementById('loading')!;
const $error = document.getElementById('error')!;
const $errorText = document.getElementById('error-text')!;
const $results = document.getElementById('results')!;
const $videoList = document.getElementById('video-list')! as HTMLUListElement;
const $audioList = document.getElementById('audio-list')! as HTMLUListElement;
const $videoEmpty = document.getElementById('video-empty')!;
const $audioEmpty = document.getElementById('audio-empty')!;
const $pageTitle = document.getElementById('page-title')!;
const $pageUrl = document.getElementById('page-url')!;
const $retry = document.getElementById('retry') as HTMLButtonElement;
const $cancel = document.getElementById('cancel') as HTMLButtonElement;
const $audioFormat = document.getElementById('audio-format') as HTMLSelectElement;

$pageUrl.textContent = videoUrl;
$cancel.addEventListener('click', () => window.close());
$retry.addEventListener('click', () => probe());

function show(which: 'loading' | 'error' | 'results') {
    $loading.hidden = which !== 'loading';
    $error.hidden = which !== 'error';
    $results.hidden = which !== 'results';
}

function fmtBytes(bytes?: number): string {
    if (!bytes || bytes <= 0) return '—';
    const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
    let v = bytes, u = 0;
    while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
    return `${v.toFixed(1)} ${units[u]}`;
}

function metaLine(f: FormatOption): string {
    const parts: string[] = [];
    if (f.ext) parts.push(f.ext);
    if (f.vcodec) parts.push(f.vcodec);
    if (f.acodec) parts.push(f.acodec);
    if (f.fps && f.fps > 30) parts.push(`${f.fps}fps`);
    return parts.join(' · ');
}

function renderRow(f: FormatOption, onPick: (f: FormatOption) => void): HTMLLIElement {
    const li = document.createElement('li');
    li.className = 'format-row';

    const left = document.createElement('div');
    left.className = 'format-left';
    const label = document.createElement('div');
    label.className = 'format-label';
    label.textContent = f.label;
    const meta = document.createElement('div');
    meta.className = 'format-meta';
    meta.textContent = metaLine(f);
    left.append(label, meta);

    const size = document.createElement('div');
    size.className = 'format-size';
    size.textContent = fmtBytes(f.fileSize);

    li.append(left, size);
    li.addEventListener('click', () => onPick(f));
    return li;
}

let videoTitle: string | undefined;

function render(resp: ListFormatsResponse) {
    videoTitle = resp.title ?? undefined;
    $pageTitle.textContent = resp.title ?? videoUrl;

    $videoList.innerHTML = '';
    $audioList.innerHTML = '';

    const videos = resp.formats.filter(f => f.kind === 'video');
    const audios = resp.formats.filter(f => f.kind === 'audio');

    for (const f of videos) $videoList.appendChild(renderRow(f, pick));
    for (const f of audios) $audioList.appendChild(renderRow(f, pick));

    $videoEmpty.hidden = videos.length > 0;
    $audioEmpty.hidden = audios.length > 0;

    show('results');
}

function pick(f: FormatOption) {
    // For an audio-only row, honour the "Convert to" selector. Video rows keep
    // their container regardless of the audio selector.
    const audioFormat =
        f.kind === 'audio' && $audioFormat.value ? $audioFormat.value : undefined;

    // The saved file's extension is the target container when converting,
    // otherwise the source stream's own ext.
    const outExt = audioFormat ?? f.ext;

    // Pretty label that survives the round-trip into the AddDownloadDialog's
    // "Format: …" row. When converting, the source size no longer matches the
    // output, so show the target format instead of a misleading size.
    const sizeLabel = f.fileSize ? ` · ${fmtBytes(f.fileSize)}` : '';
    const prettyLabel = audioFormat
        ? `${f.label} → ${audioFormat.toUpperCase()}`
        : `${f.label} · ${f.ext.toUpperCase()}${sizeLabel}`;

    // Use the video title as the file name so the dialog shows something
    // meaningful (not "download.bin"). Sanitize characters Windows forbids
    // in filenames; fall back to a generic name if there's no title.
    const safeTitle = (videoTitle ?? '')
        .replace(/[\\/:*?"<>|]+/g, ' ')
        .replace(/\s+/g, ' ')
        .trim()
        .slice(0, 120);
    const fileName = safeTitle ? `${safeTitle}.${outExt}` : undefined;

    const request: DownloadRequest = {
        type: 'download',
        url: videoUrl,
        fileName,
        ytDlpFormat: f.formatId,
        ytDlpFormatLabel: prettyLabel,
        ytDlpAudioFormat: audioFormat,
        userAgent: navigator.userAgent,
        referer: videoUrl,
        // ext drives the AddDownloadDialog's filename guess + folder memory.
        mime: undefined,
        // A transcode changes the size, so don't seed a wrong total.
        expectedLength: audioFormat ? undefined : f.fileSize,
    };

    chrome.runtime.sendMessage(
        { type: 'sidm:capture-manifest', request },
        (resp) => {
            if (resp?.ok) {
                window.close();
            } else {
                $errorText.textContent =
                    `Couldn't send to SIDM: ${resp?.error ?? 'unknown error'}`;
                show('error');
            }
        },
    );
}

function probe() {
    if (!videoUrl) {
        $errorText.textContent = 'No URL was passed to the picker.';
        show('error');
        return;
    }
    show('loading');
    chrome.runtime.sendMessage(
        { type: 'sidm:list-formats', url: videoUrl },
        (resp) => {
            if (chrome.runtime.lastError) {
                $errorText.textContent = chrome.runtime.lastError.message ?? 'Background script unreachable.';
                show('error');
                return;
            }
            if (!resp?.ok) {
                const detail = resp?.detail ? `: ${resp.detail}` : '';
                $errorText.textContent =
                    `${resp?.error ?? 'Probe failed'}${detail}`;
                show('error');
                return;
            }
            render(resp.response as ListFormatsResponse);
        },
    );
}

probe();
