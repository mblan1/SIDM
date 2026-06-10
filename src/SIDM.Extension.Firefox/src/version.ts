/**
 * Shared extension-version comparison. Used by the background worker (to
 * self-reload when SIDM bundles a newer extension) and by the popup (to offer
 * a manual "Reload" so the user never has to visit chrome://extensions).
 */

/**
 * Returns true when `bundled` is a strictly newer extension than `installed`.
 * Plain dotted-int compare ("0.1.16" < "0.1.17"; "0.2.0" > "0.1.99").
 * Pre-release suffixes fall back to ordinal compare on the leftover — close
 * enough for the upgrade signal.
 */
export function isExtensionOutdated(installed: string, bundled: string): boolean {
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
