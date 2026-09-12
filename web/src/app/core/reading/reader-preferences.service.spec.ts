import { ReaderPreferencesService } from './reader-preferences.service';

describe('ReaderPreferencesService', () => {
  beforeEach(() => localStorage.clear());

  describe('page-navigation animation', () => {
    it('defaults to slide when nothing is stored', () => {
      const svc = new ReaderPreferencesService();
      expect(svc.pageAnimation()).toBe('slide');
    });

    it('persists and reloads the chosen transition', () => {
      const a = new ReaderPreferencesService();
      a.setPageAnimation('reveal');
      expect(a.pageAnimation()).toBe('reveal');
      expect(localStorage.getItem(ReaderPreferencesService.PageAnimationKey)).toBe('reveal');

      // A fresh service instance (e.g. a later session) reads the stored value.
      expect(new ReaderPreferencesService().pageAnimation()).toBe('reveal');
    });

    it('accepts none and slide as well', () => {
      const svc = new ReaderPreferencesService();
      svc.setPageAnimation('none');
      expect(new ReaderPreferencesService().pageAnimation()).toBe('none');
      svc.setPageAnimation('slide');
      expect(new ReaderPreferencesService().pageAnimation()).toBe('slide');
    });

    it('falls back to the default for an unrecognised stored value', () => {
      localStorage.setItem(ReaderPreferencesService.PageAnimationKey, 'bogus');
      expect(new ReaderPreferencesService().pageAnimation()).toBe('slide');
    });
  });

  describe('onboarding help-seen flag', () => {
    it('reports unseen on a fresh device', () => {
      expect(new ReaderPreferencesService().hasSeenHelp()).toBe(false);
    });

    it('remembers once marked seen (persists across instances)', () => {
      const a = new ReaderPreferencesService();
      a.markHelpSeen();
      expect(a.hasSeenHelp()).toBe(true);
      expect(new ReaderPreferencesService().hasSeenHelp()).toBe(true);
    });
  });
});
