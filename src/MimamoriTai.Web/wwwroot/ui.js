// Tiny cookie helpers used to persist the selected household id across page
// refreshes. Only called from OnAfterRenderAsync (never during prerendering),
// so there is no need to guard against a missing `document` here.
window.mimamoriUi = {
    getCookie: function (name) {
        const match = document.cookie.match(new RegExp('(?:^|; )' + name + '=([^;]*)'));
        return match ? decodeURIComponent(match[1]) : null;
    },
    setCookie: function (name, value, days) {
        const maxAgeDays = days || 365;
        const expires = new Date(Date.now() + maxAgeDays * 86400000).toUTCString();
        document.cookie = name + '=' + encodeURIComponent(value) + '; expires=' + expires + '; path=/; SameSite=Lax';
    }
};
