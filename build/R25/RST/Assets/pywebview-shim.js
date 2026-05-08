// pywebview-shim.js — bridges the legacy pyRevit-era HTML/JS to WebView2.
//
// The vendored profile_loader.html was authored against pywebview's
// `window.pywebview.api` and waits on a `pywebviewready` event. WebView2
// gives us `chrome.webview.hostObjects.api` that returns Promises just
// like pywebview, so a thin alias + a synthetic ready event is enough
// to keep the legacy JS unchanged.
//
// Injected via CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync so
// it runs before the page's own scripts.

(function () {
    if (!window.chrome || !window.chrome.webview || !window.chrome.webview.hostObjects) {
        console.error('[RST] WebView2 hostObjects unavailable — bridge will not function.');
        return;
    }
    // The host object exposes PascalCase methods (idiomatic C#); the legacy
    // JS calls snake_case (idiomatic Python). Proxy translates names and
    // JSON-encodes args / decodes returns so the wire is plain strings —
    // avoids COM marshaling quirks for complex DTOs.
    var raw = window.chrome.webview.hostObjects.api;
    function snakeToPascal(name) {
        return name.replace(/(^|_)([a-z0-9])/g, function (_m, _u, c) { return c.toUpperCase(); });
    }
    window.pywebview = {
        api: new Proxy({}, {
            get: function (_target, name) {
                if (typeof name !== 'string') return undefined;
                var hostName = snakeToPascal(name);
                return async function () {
                    var args = Array.prototype.slice.call(arguments)
                        .map(function (a) { return JSON.stringify(a); });
                    var json = await raw[hostName].apply(raw, args);
                    if (json === undefined || json === null || json === '') return undefined;
                    try { return JSON.parse(json); }
                    catch (e) {
                        console.error('[RST] bridge return JSON.parse failed for ' + name + ':', json);
                        return undefined;
                    }
                };
            }
        })
    };

    // The legacy code: window.addEventListener('pywebviewready', init).
    // hostObjects are available immediately, so fire on next tick after DOM
    // attaches the listener. DOMContentLoaded covers both fast and slow
    // page-parse cases.
    function fireReady() {
        try { window.dispatchEvent(new Event('pywebviewready')); }
        catch (e) { console.error('[RST] pywebviewready dispatch failed:', e); }
    }
    if (document.readyState === 'complete' || document.readyState === 'interactive') {
        setTimeout(fireReady, 0);
    } else {
        window.addEventListener('DOMContentLoaded', function () { setTimeout(fireReady, 0); });
    }
})();
