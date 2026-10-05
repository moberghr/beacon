/**
 * Server-advertised (`/auth/me`) when the host owns sign-in: Beacon's own login page and Sign out
 * are never shown, and a signed-out user is sent to the host's login page instead.
 */
export interface ExternalLogin {
  loginUrl: string;
  returnUrlParameter: string;
}

/**
 * The host login URL with `returnTo` attached. `returnTo` must be the full path the browser should
 * come back to — including the router basename — because the host, not the SPA router, performs
 * the post-login redirect.
 */
export function externalLoginHref(
  externalLogin: ExternalLogin,
  returnTo: string,
): string {
  const url = new URL(externalLogin.loginUrl, window.location.origin);
  url.searchParams.set(externalLogin.returnUrlParameter, returnTo);
  return url.origin === window.location.origin
    ? `${url.pathname}${url.search}${url.hash}`
    : url.toString();
}

/** Full-page navigation to the host's login page; the SPA router cannot reach it. */
export function redirectToExternalLogin(
  externalLogin: ExternalLogin,
  returnTo: string,
): void {
  window.location.assign(externalLoginHref(externalLogin, returnTo));
}
