import { automaticLabel, cardSource, modeLabel, webCoverLabel, webUnavailableLabel } from './cover-labels';

/** The picker's words (1.29.0): the automatic line, the card source after a change, the web part's reasons. */
describe('cover labels', () => {
  it('says what the automatic layer uses, by reason first', () => {
    expect(automaticLabel({ mode: 'Automatic', autoSource: 'Crop', reason: 'Spread' })).toContain('front half of page 1');
    expect(automaticLabel({ mode: 'Automatic', autoSource: 'WebVolume', reason: 'SeriesVolume1' })).toContain('volume 1');
    // 1.30.0: a linked series folder shows its own volume 1 as that card shows it (crop, web cover or page 1).
    expect(automaticLabel({ mode: 'Automatic', autoSource: 'Crop', reason: 'SeriesLocalVolume1' })).toBe('the cover of your volume 1 (as its own card shows it)');
    expect(automaticLabel({ mode: 'Automatic', autoSource: 'Poster' })).toBe('the stored series poster');
    expect(automaticLabel({ mode: 'Automatic' })).toBe("this file's cover");
    expect(automaticLabel(null)).toBe('');
  });

  it('maps a state to the card source the card shows', () => {
    expect(cardSource({ mode: 'Automatic', autoSource: 'Crop' })).toBe('Crop');
    expect(cardSource({ mode: 'Automatic' })).toBe('File');
    expect(cardSource({ mode: 'FilePinned' })).toBe('File');
    expect(cardSource({ mode: 'Archive' })).toBe('Chosen');
    expect(cardSource({ mode: 'VolumeCover' })).toBe('Chosen');
  });

  it('names every mode', () => {
    for (const mode of ['Automatic', 'FilePinned', 'Archive', 'VolumeCover', 'Crop'] as const) {
      expect(modeLabel(mode).length).toBeGreaterThan(3);
    }
  });

  it('explains why there are no web covers', () => {
    expect(webUnavailableLabel('not_linked')).toContain('identified');
    expect(webUnavailableLabel('dont_match')).toContain("Don't match");
    expect(webUnavailableLabel('volume_covers_off')).toContain('Volume covers from the web');
    expect(webUnavailableLabel('web_covers_hidden')).toContain('saved web covers');
    expect(webUnavailableLabel('something_new')).toContain('not available');
  });

  it('labels a web cover by language, edition and download state (no provider credit on the tile)', () => {
    expect(webCoverLabel({ id: 'vc1', kind: 'Volume', volume: 1, variant: 0, locale: 'ja', stored: true })).toBe('JA');
    expect(webCoverLabel({ id: 'vc2', kind: 'Volume', volume: 3, variant: 1, locale: 'en', stored: false }))
      .toBe('EN · edition 1 · not downloaded yet');
    expect(webCoverLabel({ id: 'vc3', kind: 'Main', locale: 'ko', stored: true })).toBe('Series cover · KO');
  });
});
