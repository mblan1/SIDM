/**
 * Wire-format mirror of SIDM.Ipc (C#). The "type" field is the discriminator;
 * everything is JSON over the native messaging stdio framing.
 */

export type IpcMessage =
    | HelloRequest
    | HelloResponse
    | DownloadRequest
    | DownloadResponse
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
}

export interface DownloadResponse {
    type: 'download-response';
    downloadId: number;
    status: string;
}

export interface ErrorMessage {
    type: 'error';
    reason: string;
    detail?: string;
}

export const NATIVE_HOST_ID = 'com.sidm.host';
