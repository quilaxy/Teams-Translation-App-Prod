(function () {
    window.teamsTabConfig = window.teamsTabConfig || {};

    window.teamsTabConfig.initialize = function () {
        if (typeof microsoftTeams === "undefined") {
            return;
        }

        microsoftTeams.app.initialize().then(function () {
            microsoftTeams.pages.config.registerOnSaveHandler(function (saveEvent) {
                microsoftTeams.pages.config.setConfig({
                    entityId: "liveTranslationMeeting",
                    suggestedDisplayName: "KID AI Live Translation",
                    contentUrl: window.location.origin + "/translation-tab",
                    websiteUrl: window.location.origin + "/translation-tab"
                }).then(function () {
                    saveEvent.notifySuccess();
                }).catch(function (error) {
                    console.error("[TEAMS] Failed to save tab configuration:", error);
                    saveEvent.notifyFailure("Unable to save tab configuration. Please retry.");
                });
            });

            microsoftTeams.pages.config.setValidityState(true);
        });
    };
})();
