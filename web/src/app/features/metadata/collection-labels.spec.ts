import { collectionLabel, collectionResultMessage, isOwnCollection } from './collection-labels';
import { ownSeriesInfo } from './metadata-state.service';
import { seriesInfo } from './series-info.testing';

/** "Collection about" wording (1.34.0). */
describe('collection labels', () => {
  it('names the series a collection is about', () => {
    expect(collectionLabel('Starlight Academy')).toBe('Collection about Starlight Academy');
    expect(collectionLabel('  ')).toBe('Collection about a series');
  });

  it('says what marking a folder did', () => {
    expect(collectionResultMessage('Starlight Academy', { change: { nodeId: 'f1' }, queued: 1, contentSet: false }))
      .toBe('Collection about Starlight Academy - 1 work inside will be matched');
    expect(collectionResultMessage('Starlight Academy', { change: { nodeId: 'f1' }, queued: 0, contentSet: true }))
      .toBe('Collection about Starlight Academy - the works inside are matched when automatic matching runs - Content set to Doujinshi & adult one-shots');
  });

  it('a folder\'s own collection is its own series information; an inherited one is not', () => {
    const web = { provider: 'mangaupdates', providerName: 'MangaUpdates', fetchedAt: 'x', hasImage: false };
    const own = seriesInfo({ state: 'CollectionAbout', web, link: { state: 'CollectionAbout', nodeId: 'n1', inherited: false } });
    expect(isOwnCollection(own)).toBe(true);
    expect(ownSeriesInfo(own)).toBe(true);
    const below = seriesInfo({ state: 'None', link: { state: 'CollectionAbout', nodeId: 'p1', inherited: true } });
    expect(isOwnCollection(below)).toBe(false);
    expect(ownSeriesInfo(below)).toBe(false);
  });
});
