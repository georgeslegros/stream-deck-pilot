// The browser's time zone, asked once per circuit so the server-rendered dashboard (#99) shows
// local times. Nothing here runs on a timer: ages refresh with each pushed update.
window.sdpClock = {
    zone: () => ({
        id: Intl.DateTimeFormat().resolvedOptions().timeZone || null,
        offsetMinutes: new Date().getTimezoneOffset(),
    }),
};

// Copying a new API key (#98). The clipboard API needs a secure context (HTTPS or localhost); on
// plain HTTP it's missing and the page asks to copy by hand, with the key selected.
window.sdpClipboard = {
    copy: async (text) => {
        try {
            if (!navigator.clipboard) return false;
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    },
    select: (selector) => document.querySelector(selector)?.select(),
};
