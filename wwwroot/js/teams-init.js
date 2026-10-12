(function () {
    // Class ditaruh di <html> bukan <body>
    // karena Blazor mengelola <body> — selector CSS di razor pakai .teams-light/.teams-dark
    function applyTheme(theme) {
        var html = document.documentElement;
        html.classList.remove("teams-dark", "teams-light", "teams-contrast");
        if (!theme || theme === "dark") {
            html.classList.add("teams-dark");
        } else if (theme === "default") {
            html.classList.add("teams-light");
        } else if (theme === "contrast") {
            html.classList.add("teams-contrast");
        }
        console.log("[TEAMS] Theme:", theme);
    }

    if (typeof microsoftTeams === "undefined") {
        console.warn("[TEAMS] SDK not loaded — standalone mode");
        applyTheme("dark");
        return;
    }

    microsoftTeams.app.initialize().then(function () {
        microsoftTeams.app.getContext().then(function (context) {
            applyTheme(context.app.theme);
            window.teamsMeetingId   = (context.meeting && context.meeting.id) ? context.meeting.id : "default";
            window.teamsUserId      = (context.user && context.user.id) ? context.user.id : "";
            window.teamsUserName    = (context.user && context.user.displayName) ? context.user.displayName : "";
            console.log("[TEAMS] Meeting ID:", window.teamsMeetingId);
        }).catch(function () { applyTheme("dark"); });

        microsoftTeams.app.registerOnThemeChangeHandler(applyTheme);
        microsoftTeams.app.notifySuccess();
    }).catch(function () { applyTheme("dark"); });
})();