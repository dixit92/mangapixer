import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The consent texts of tracker sync (1.43.0), version 1 of each stream (`TRACKER_ADMIN_CONSENT_VERSION` /
 * `TRACKER_USER_CONSENT_VERSION` in `tracker-texts.ts` - bump them with any change here).
 *
 * DRAFT (lane U, 2026-10-14): lane E's DRAFT wording (Tracker Sync Engine note, "DRAFT - consent texts"), corrected with lane A's
 * adapter facts (MyAnimeList receives no dates; MyAnimeList documents no way for an app to end its sign-in). The integrator applies
 * the owner-approved text here before the release.
 */
@Component({
  selector: 'app-tracker-consent-text',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="consent" [attr.data-testid]="'tracker-consent-' + audience() + '-' + slug()">
      @switch (audience() + ':' + slug()) {
        @case ('admin:mal') {
          <p><strong>Allow users to sync their reading progress to MyAnimeList.</strong> When on, each user of this server can connect
            their own MyAnimeList account in Settings, and MangaPixer updates their MyAnimeList list while they read. Nothing is sent
            for a user who has not connected an account.</p>
          <p><strong>For personal servers only.</strong> MyAnimeList's API terms are for personal, non-commercial use: turn this on
            only on a personal, self-hosted server or a personal desktop install - not on a server you run for the public or for a
            business.</p>
          <p><strong>What is sent, for a connected user only:</strong> the MyAnimeList number of a series they read a chapter or
            volume of, how many chapters and volumes they have read, and a status (Reading, or Completed when they reach the end
            MyAnimeList lists for a finished series) - with their own MyAnimeList sign-in. Before each update MangaPixer reads that
            user's entry for the series to compare. Never titles, file or folder names, paths, other users, or anything that
            identifies this server. Requests go to api.myanimelist.net and, when connecting, myanimelist.net; they name MangaPixer
            and its version.</p>
          <p><strong>Register this server with MyAnimeList</strong> (<a href="https://myanimelist.net/apiconfig/create" target="_blank" rel="noopener noreferrer">myanimelist.net/apiconfig/create</a>): MangaPixer does not ship a
            shared key. Enter the Client ID you get, and register the redirect URL shown below exactly.</p>
          <p>Turning this off stops all syncing to MyAnimeList at once; users keep their connection and syncing resumes when you turn
            it back on.</p>
        }
        @case ('admin:mangaupdates') {
          <p><strong>Allow users to sync their reading progress to MangaUpdates.</strong> When on, each user of this server can
            connect their own MangaUpdates account in Settings, and MangaPixer updates their MangaUpdates reading list while they
            read. Nothing is sent for a user who has not connected an account. This is separate from "Fetch from the web".</p>
          <p><strong>What is sent, for a connected user only:</strong> the MangaUpdates number of a series they read a chapter or
            volume of (the one this server's series link already uses), how many chapters and volumes they have read, and the list
            it belongs on (Reading, or Complete for a finished series) - with their own MangaUpdates sign-in. No dates. Before each
            update MangaPixer reads that user's list entry to compare. Never titles, file or folder names, paths, other users, or
            anything that identifies this server. Requests go to api.mangaupdates.com.</p>
          <p><strong>Connecting asks each user for their MangaUpdates username and password once:</strong> MangaPixer passes them
            to MangaUpdates to get a sign-in token and never stores them. No registration needed.</p>
          <p>Turning this off stops all syncing to MangaUpdates at once.</p>
        }
        @case ('user:mal') {
          <p><strong>Sync your reading progress to MyAnimeList.</strong> While you read, MangaPixer updates your MyAnimeList list
            when you finish a chapter or volume of a series this server knows the MyAnimeList number of.</p>
          <p><strong>Sent to MyAnimeList, with your account:</strong> which series (its MyAnimeList number), how many chapters and
            volumes you have read, and Reading or Completed. MangaPixer first reads your entry for that series to compare.
            <strong>Never sent:</strong> titles, file names, which files you read, your other activity on this server, or anything
            about other users.</p>
          <p><strong>What MangaPixer will not do:</strong> lower a number on MyAnimeList without asking you; change a series you put
            on hold, dropped, completed or are rereading there; mark an ongoing series Completed; or sync your
            <strong>Private</strong> libraries or doujinshi, unless you turn sync on for that folder or series. Folders you mark
            read all at once, and imported reading, wait for you to press Send. Series you start here are added to your list unless
            you turn that off.</p>
          <p><strong>Your sign-in:</strong> you sign in on myanimelist.net - MangaPixer never sees your password. It keeps the
            sign-in token MyAnimeList gives it encrypted on this server, renews it before it expires, uses it only for these
            updates, never shows it again, and deletes it when you disconnect.</p>
          <p>The server's admin can see that you connected an account and can disconnect it, but cannot see or use your
            sign-in.</p>
        }
        @case ('user:mangaupdates') {
          <p><strong>Sync your reading progress to MangaUpdates.</strong> While you read, MangaPixer updates your MangaUpdates
            reading list when you finish a chapter or volume of a series this server links to MangaUpdates.</p>
          <p><strong>Sent to MangaUpdates, with your account:</strong> which series (its MangaUpdates number), how many chapters and
            volumes you have read, and the list (Reading, or Complete). No dates. MangaPixer first reads your list entry to compare.
            <strong>Never sent:</strong> titles, file names, which files you read, your other activity on this server, or anything
            about other users.</p>
          <p><strong>What MangaPixer will not do:</strong> lower a number on MangaUpdates without asking you; change a series you
            put on hold, dropped or completed there; mark an ongoing series Complete; or sync your <strong>Private</strong>
            libraries or doujinshi, unless you turn sync on for that folder or series. Folders you mark read all at once, and
            imported reading, wait for you to press Send. Series you start here are added to your list unless you turn that
            off.</p>
          <p><strong>Your password:</strong> MangaUpdates has no other way to connect an app, so MangaPixer asks for your
            MangaUpdates username and password ONCE, passes them to MangaUpdates to get a sign-in token, and never stores or logs
            them. The token is stored encrypted, used only for these updates, never shown again, and deleted when you disconnect
            (MangaPixer then signs it out on MangaUpdates).</p>
          <p>The server's admin can see that you connected an account and can disconnect it, but cannot see or use your
            sign-in.</p>
        }
      }
    </div>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .consent { font-size: 13px; color: var(--mp-text-secondary); border-left: 3px solid var(--mp-text-faint); padding: 2px 12px;
               margin: 6px 0 10px; overflow-wrap: anywhere; }
    .consent p { margin: 6px 0; }
  `],
})
export class TrackerConsentTextComponent {
  readonly slug = input.required<string>();
  readonly audience = input.required<'admin' | 'user'>();
}
