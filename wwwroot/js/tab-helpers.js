window.tabHelpers = {
    popoutWindow: null,
    POPUP_BOTTOM_MARGIN: 60,
    POPOUT_DEFAULT_W: 700,
    // 200px content + ~70px Chrome toolbar = 270px total
    // Ini cukup untuk committed + active area, tidak terlalu menutupi share screen
    POPOUT_DEFAULT_H: 100,

    isScrolledToBottom: function (el) {
        if (!el) return true;
        return el.scrollTop + el.clientHeight >= el.scrollHeight - 10;
    },
    scrollToBottom: function (el) {
        if (el) el.scrollTop = el.scrollHeight;
    },
    copyToClipboard: function (text) {
        return navigator.clipboard.writeText(text);
    },
    getPreference: function (key) {
        try {
            return localStorage.getItem(key);
        } catch (error) {
            console.warn("getPreference failed:", error);
            return null;
        }
    },
    setPreference: function (key, value) {
        try {
            localStorage.setItem(key, value);
        } catch (error) {
            console.warn("setPreference failed:", error);
        }
    },
    openPopout: function (savedState, preferences) {
        if (this.popoutWindow && !this.popoutWindow.closed) {
            this.popoutWindow.focus();
            return;
        }

        const state = savedState || {};
        const w = state.w || this.POPOUT_DEFAULT_W;
        // Height tidak di-restore dari saved state karena Chrome outer/inner
        // mismatch menyebabkan height bertambah setiap cycle
        const h = this.POPOUT_DEFAULT_H;
        const x = state.x != null ? state.x : Math.round((screen.width - w) / 2);
        const y = state.y != null ? state.y : screen.height - h - this.POPUP_BOTTOM_MARGIN;

        const features = [
            "width=" + w, "height=" + h,
            "left=" + x, "top=" + y,
            "resizable=yes", "scrollbars=no",
            "status=no", "toolbar=no",
            "menubar=no", "location=no"
        ].join(",");

        const params = new URLSearchParams({ popout: "true" });
        if (preferences && preferences.inputLang) {
            params.set("inputLang", preferences.inputLang);
        }
        if (preferences && preferences.outputLang) {
            params.set("outputLang", preferences.outputLang);
        }
        if (preferences && preferences.meetingId) {
            params.set("meetingId", preferences.meetingId);
        }

        this.popoutWindow = window.open(
            "/translation-tab?" + params.toString(),
            "kidai_translation_popout",
            features
        );

        if (typeof microsoftTeams !== "undefined"
            && microsoftTeams.app
            && typeof microsoftTeams.app.closeTab === "function") {
            try {
                microsoftTeams.app.closeTab();
            } catch (error) {
                console.warn("closeTab failed:", error);
            }
        }
    },
    getPopoutState: function () {
        try {
            const raw = localStorage.getItem("kidai_popout_state");
            return raw ? JSON.parse(raw) : null;
        } catch {
            return null;
        }
    },
    savePopoutState: function (state) {
        try {
            localStorage.setItem("kidai_popout_state", JSON.stringify(state));
        } catch { }
    },
    registerPopoutStateListener: function () {
        window.addEventListener("beforeunload", function () {
            window.tabHelpers.savePopoutState({
                x: window.screenX,
                y: window.screenY,
                w: window.innerWidth
            });
        });
    }
};
