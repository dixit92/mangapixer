import { trackerSeriesUrl } from '../trackers/tracker-texts';

/** A MangaDex title id (a UUID). */
const MANGADEX_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * 1.44.0 (owner): a series record's own page on its site, for a link in the review rows (like the trackers' links, 1.43.2) - MangaUpdates
 * `https://www.mangaupdates.com/series/<base36 id>`, MangaDex `https://mangadex.org/title/<id>`; null for another site or an id that is not
 * one. The browser opens it in a new tab on the user's click, without a referrer; MangaPixer makes no request for it.
 */
export function providerRecordUrl(provider: string | null | undefined, externalId: string | null | undefined): string | null {
  if (!provider || !externalId) return null;
  switch (provider) {
    case 'mangaupdates': return trackerSeriesUrl('mangaupdates', externalId);
    case 'mangadex': return MANGADEX_ID.test(externalId) ? `https://mangadex.org/title/${externalId.toLowerCase()}` : null;
    default: return null;
  }
}

/** The site's name for the link's label ("Open on MangaUpdates: ..."). */
export function providerSiteName(provider: string | null | undefined): string {
  return provider === 'mangadex' ? 'MangaDex' : provider === 'mangaupdates' ? 'MangaUpdates' : 'the site';
}
