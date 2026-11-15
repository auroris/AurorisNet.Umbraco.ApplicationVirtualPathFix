// The import map points @umbraco-cms/backoffice/auth at this module. It re-exports Umbraco's module, available under
// the upstream-auth name, and overrides UmbAuthContext with the subclass below.
import { UmbAuthContext as UmbracoAuthContext } from "@aurorisnet/umbraco-application-virtual-path-fix/upstream-auth";
export * from "@aurorisnet/umbraco-application-virtual-path-fix/upstream-auth";

// Under a virtual path, the page supplies only its path base. The server URL combines it with the origin the browser
// loaded the page from, as Umbraco does at the root. umb-app imports this module, so this code runs before umb-app is
// defined and reads server-url.
const app = document.querySelector("umb-app[data-path-base]");
if (app) app.setAttribute("server-url", location.origin + app.dataset.pathBase);

/**
 * Runs Umbraco's initial token refresh only on pages where a session may exist.
 *
 * Umbraco 18.3 refreshes on the first visit and on the OAuth callback, before any tokens exist. The server marks the
 * page with data-restore-session when the refresh cookie arrived, or when the browser may have withheld it (a
 * SameSite=Strict cookie on a navigation that another site started). Tokens stay in HTTP-only cookies, and Umbraco
 * still validates them. After sign-in, the initial refresh and later session renewal run Umbraco's code.
 */
export class UmbAuthContext extends UmbracoAuthContext {
    #host;

    /**
     * Creates Umbraco's auth context and keeps its host element, which carries the server's session hint.
     * @param {HTMLElement} host The umb-app element that creates the context.
     * @param {...*} args Umbraco's other constructor arguments.
     */
    constructor(host, ...args) {
        super(host, ...args);
        this.#host = host;
    }

    /**
     * Restores the session with Umbraco's code when the page has data-restore-session. On other pages it returns at
     * once, and the backoffice starts sign-in.
     * @returns {Promise<void>}
     */
    async setInitialState() {
        if (this.#host.hasAttribute("data-restore-session")) await super.setInitialState();
    }
}
