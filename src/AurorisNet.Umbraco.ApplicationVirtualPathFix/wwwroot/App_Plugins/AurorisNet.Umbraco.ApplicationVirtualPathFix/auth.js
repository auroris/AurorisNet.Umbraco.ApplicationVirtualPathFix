import { UmbAuthContext as UmbracoAuthContext } from "@aurorisnet/umbraco-application-virtual-path-fix/upstream-auth";
export * from "@aurorisnet/umbraco-application-virtual-path-fix/upstream-auth";

// Under a virtual path the page names only its path base. The server URL takes the origin the browser loaded
// the page from, as Umbraco does at the root. umb-app imports this module, so this runs before umb-app is
// defined and reads server-url.
const app = document.querySelector("umb-app[data-path-base]");
if (app) app.setAttribute("server-url", location.origin + app.dataset.pathBase);

// Umbraco 18.3 refreshes on both the first visit and the OAuth callback, before tokens exist.
// The server marks pages where a session may exist: the refresh cookie was sent, or the browser
// may have withheld it (SameSite=Strict on a navigation another site started). Token contents
// stay HTTP-only and validation stays with Umbraco. Once signed in, initial refresh and later
// session renewal use the original code.
export class UmbAuthContext extends UmbracoAuthContext {
    #host;

    constructor(host, ...args) {
        super(host, ...args);
        this.#host = host;
    }

    async setInitialState() {
        if (this.#host.hasAttribute("data-restore-session")) await super.setInitialState();
    }
}
