/** The canonical host the committed `.htaccess` names. */
export declare const SOURCE_CANONICAL_HOST: string
/** Points the canonical-host rules at `origin`; an origin under a path drops them. */
export declare function rewriteHtaccessHost(text: string, origin: string): string
/** Rewrites the `.htaccess` Vite copied into `outDir`. */
export declare function writeSiteHtaccess(outDir: string | undefined, origin: string): void
