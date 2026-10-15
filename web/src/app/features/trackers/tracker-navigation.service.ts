import { Injectable } from '@angular/core';

/**
 * Leaves the app for a tracker's sign-in page (1.43.0). Only an https address is followed - the server builds it, but a page
 * never navigates to anything else (no `javascript:` or plain-http sign-in). Its own service so tests can see the address without
 * leaving the page.
 */
@Injectable({ providedIn: 'root' })
export class TrackerNavigationService {
  /** Returns false (and stays) when the address is not https. */
  openSignIn(url: string): boolean {
    let parsed: URL;
    try {
      parsed = new URL(url);
    } catch {
      return false;
    }
    if (parsed.protocol !== 'https:') return false;
    globalThis.location.assign(parsed.href);
    return true;
  }
}
