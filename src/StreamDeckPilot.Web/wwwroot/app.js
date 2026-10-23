// The browser's time zone, asked once per circuit so the server-rendered dashboard (#99) shows
// local times. Nothing here runs on a timer: ages refresh with each pushed update.
window.sdpClock = {
    zone: () => ({
        id: Intl.DateTimeFormat().resolvedOptions().timeZone || null,
        offsetMinutes: new Date().getTimezoneOffset(),
    }),
};
