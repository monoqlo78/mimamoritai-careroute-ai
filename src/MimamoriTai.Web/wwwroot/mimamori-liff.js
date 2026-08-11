// LIFF (LINE Front-end Framework) bootstrap for the /liff page.
//
// Loaded as a module on demand so that nothing here costs anything on the normal web
// pages: the LINE SDK is only fetched when someone actually opens the app from a LINE
// talk. Everything returns a plain result object rather than throwing, because a LIFF
// view that fails to initialise must degrade into a readable "open me from LINE"
// screen, not a blank WebView with an error in a console nobody can see.

const SDK_URL = "https://static.line-scdn.net/liff/edge/2/sdk.js";

let sdkPromise = null;

function loadSdk() {
    if (window.liff) {
        return Promise.resolve(window.liff);
    }

    sdkPromise ??= new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = SDK_URL;
        script.async = true;
        script.onload = () => resolve(window.liff);
        script.onerror = () => {
            // Allow a later retry: a WebView that lost signal mid-load should not be
            // stuck with a permanently rejected cached promise.
            sdkPromise = null;
            reject(new Error("LIFF SDK could not be loaded."));
        };
        document.head.appendChild(script);
    });

    return sdkPromise;
}

/**
 * Initialises LIFF and returns the ID token for server-side verification.
 *
 * The ID token - not the profile - is what gets sent to the server, because
 * `liff.getProfile()` only tells the *page* who the user is and any userId posted from
 * the browser could be forged. The token is signed by LINE and re-validated server-side.
 *
 * @param {string} liffId The LIFF app id from LINE Developers.
 * @returns {Promise<{status: string, idToken?: string, displayName?: string, inClient?: boolean}>}
 */
export async function initLiff(liffId) {
    if (!liffId) {
        return { status: "unconfigured" };
    }

    let liff;
    try {
        liff = await loadSdk();
        await liff.init({ liffId });
    } catch (error) {
        console.error("LIFF init failed.", error);
        return { status: "error" };
    }

    if (!liff.isLoggedIn()) {
        // Outside the LINE app this would need liff.login(), which navigates away.
        // Doing that automatically would trap a plain browser visitor in a redirect
        // they never asked for, so the page just explains itself instead.
        return { status: "signed-out", inClient: liff.isInClient() };
    }

    const idToken = liff.getIDToken();
    if (!idToken) {
        return { status: "signed-out", inClient: liff.isInClient() };
    }

    return {
        status: "ok",
        idToken,
        inClient: liff.isInClient()
    };
}

/** True when the device asks for reduced motion, so the 7.6MB GLB is never fetched. */
export function prefersReducedMotion() {
    return window.matchMedia("(prefers-reduced-motion: reduce)").matches;
}

/** Closes the LIFF WebView and returns to the talk. No-op outside the LINE app. */
export function closeWindow() {
    try {
        if (window.liff?.isInClient()) {
            window.liff.closeWindow();
        }
    } catch (error) {
        console.error("LIFF closeWindow failed.", error);
    }
}
