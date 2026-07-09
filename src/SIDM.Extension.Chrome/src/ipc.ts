/**
 * Wire-format mirror of SIDM.Ipc (C#). The "type" field is the discriminator;
 * everything is JSON over Chrome's Native Messaging stdio framing.
 */

export type IpcMessage =
    | HelloRequest
    | HelloResponse
    | DownloadRequest
    | DownloadResponse
    | ListFormatsRequest
    | ListFormatsResponse
    | ErrorMessage;

export interface HelloRequest {
    type: 'hello';
    clientName: string;
    clientVersion: string;
}

export interface HelloResponse {
    type: 'hello-response';
    appName: string;
    appVersion: string;
    capabilities: string[];
    /**
     * Version of the extension that SIDM has bundled inside its app folder
     * — set by SIDM 0.1.18 and newer. When this is greater than the
     * extension's own manifest.version, the extension self-reloads via
     * chrome.runtime.reload() so SIDM's auto-extract takes effect without
     * the user touching chrome://extensions/.
     */
    bundledExtensionVersion?: string;
}

export interface DownloadRequest {
    type: 'download';
    url: string;
    fileName?: string;
    suggestedFolder?: string;
    headers?: Record<string, string>;
    cookies?: Record<string, string>;
    referer?: string;
    userAgent?: string;
    expectedLength?: number;
    mime?: string;
    /** yt-dlp -f selector, e.g. "137+bestaudio/best". Set by the format picker. */
    ytDlpFormat?: string;
    /** Human-readable label for the chosen format ("1080p MP4 · ~145 MB"). */
    ytDlpFormatLabel?: string;
    /**
     * Target audio container for an audio-only download — "mp3", "m4a",
     * "opus", "flac", "wav", etc. Set by the picker's "Convert to" selector;
     * SIDM runs yt-dlp with `-x --audio-format <fmt>` to transcode. Absent for
     * video or a raw (unconverted) audio stream.
     */
    ytDlpAudioFormat?: string;
}

export interface DownloadResponse {
    type: 'download-response';
    downloadId: number;
    status: string;
}

/** Ask SIDM to run `yt-dlp -J <url>` and reply with available formats. */
export interface ListFormatsRequest {
    type: 'list-formats';
    url: string;
}

/** Reply to ListFormatsRequest: video title + curated format rows. */
export interface ListFormatsResponse {
    type: 'list-formats-response';
    url: string;
    title?: string;
    formats: FormatOption[];
}

export interface FormatOption {
    formatId: string;
    /** "video" or "audio". */
    kind: 'video' | 'audio';
    /** Ready-to-render row label ("1080p", "Opus · 256k"). */
    label: string;
    ext: string;
    vcodec?: string;
    acodec?: string;
    height?: number;
    fps?: number;
    audioBitrateKbps?: number;
    fileSize?: number;
}

export interface ErrorMessage {
    type: 'error';
    reason: string;
    detail?: string;
}

export const NATIVE_HOST_ID = 'com.sidm.host';
