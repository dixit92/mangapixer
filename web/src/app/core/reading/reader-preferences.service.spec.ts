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

  /**
   * 1.19.0 image scaling: two more per-device preferences, same storage contract
   * (default when unset, round-trip across instances, default for garbage).
   */
  describe('page quality', () => {
    it('defaults to auto (display-sized requests) when nothing is stored', () => {
      expect(new ReaderPreferencesService().pageQuality()).toBe('auto');
    });

    it('persists and reloads the chosen quality', () => {
      const a = new ReaderPreferencesService();
      a.setPageQuality('full');
      expect(a.pageQuality()).toBe('full');
      expect(localStorage.getItem(ReaderPreferencesService.PageQualityKey)).toBe('full');
      expect(new ReaderPreferencesService().pageQuality()).toBe('full');
      a.setPageQuality('auto');
      expect(new ReaderPreferencesService().pageQuality()).toBe('auto');
    });

    it('falls back to auto for an unrecognised stored value', () => {
      localStorage.setItem(ReaderPreferencesService.PageQualityKey, 'ultra');
      expect(new ReaderPreferencesService().pageQuality()).toBe('auto');
    });

    it('falls back to auto for an empty stored value', () => {
      localStorage.setItem(ReaderPreferencesService.PageQualityKey, '');
      expect(new ReaderPreferencesService().pageQuality()).toBe('auto');
    });
  });

  describe('upscaler (rendering)', () => {
    it('defaults to smooth (the pre-1.19.0 browser resampling)', () => {
      expect(new ReaderPreferencesService().upscaler()).toBe('smooth');
    });

    it('persists and reloads the chosen renderer', () => {
      const a = new ReaderPreferencesService();
      a.setUpscaler('enhance');
      expect(a.upscaler()).toBe('enhance');
      expect(localStorage.getItem(ReaderPreferencesService.UpscalerKey)).toBe('enhance');
      expect(new ReaderPreferencesService().upscaler()).toBe('enhance');
      a.setUpscaler('smooth');
      expect(new ReaderPreferencesService().upscaler()).toBe('smooth');
    });

    it('falls back to smooth for an unrecognised stored value', () => {
      localStorage.setItem(ReaderPreferencesService.UpscalerKey, 'anime4k');
      expect(new ReaderPreferencesService().upscaler()).toBe('smooth');
    });
  });

  /**
   * 1.20.0 "Downscale filter": which resampling filter the server uses when it
   * downscales a page. Same storage contract as the other 1.19.0 preferences
   * (default when unset, round-trip across instances, default for garbage).
   */
  describe('downscale filter', () => {
    it('defaults to balanced (server Mitchell default) when nothing is stored', () => {
      expect(new ReaderPreferencesService().downscaleFilter()).toBe('balanced');
    });

    it('persists and reloads the chosen filter', () => {
      const a = new ReaderPreferencesService();
      a.setDownscaleFilter('sharp');
      expect(a.downscaleFilter()).toBe('sharp');
      expect(localStorage.getItem(ReaderPreferencesService.DownscaleFilterKey)).toBe('sharp');
      expect(new ReaderPreferencesService().downscaleFilter()).toBe('sharp');
      a.setDownscaleFilter('soft');
      expect(new ReaderPreferencesService().downscaleFilter()).toBe('soft');
      a.setDownscaleFilter('balanced');
      expect(new ReaderPreferencesService().downscaleFilter()).toBe('balanced');
    });

    it('falls back to balanced for an unrecognised stored value', () => {
      localStorage.setItem(ReaderPreferencesService.DownscaleFilterKey, 'crunchy');
      expect(new ReaderPreferencesService().downscaleFilter()).toBe('balanced');
    });

    it('falls back to balanced for an empty stored value', () => {
      localStorage.setItem(ReaderPreferencesService.DownscaleFilterKey, '');
      expect(new ReaderPreferencesService().downscaleFilter()).toBe('balanced');
    });
  });

  describe('storage failures', () => {
    /**
     * Private-mode Safari throws from setItem. The in-memory signal must still
     * take the new value (the setting works for this session) and nothing throws.
     */
    it('keeps the in-memory value when localStorage.setItem throws', () => {
      const svc = new ReaderPreferencesService();
      const original = Storage.prototype.setItem;
      Storage.prototype.setItem = () => { throw new Error('denied'); };
      try {
        expect(() => svc.setPageQuality('full')).not.toThrow();
        expect(svc.pageQuality()).toBe('full');
        expect(() => svc.setUpscaler('enhance')).not.toThrow();
        expect(svc.upscaler()).toBe('enhance');
        expect(() => svc.setDownscaleFilter('sharp')).not.toThrow();
        expect(svc.downscaleFilter()).toBe('sharp');
      } finally {
        Storage.prototype.setItem = original;
      }
    });

    it('falls back to the defaults when localStorage.getItem throws', () => {
      const original = Storage.prototype.getItem;
      Storage.prototype.getItem = () => { throw new Error('denied'); };
      try {
        const svc = new ReaderPreferencesService();
        expect(svc.pageQuality()).toBe('auto');
        expect(svc.upscaler()).toBe('smooth');
        expect(svc.downscaleFilter()).toBe('balanced');
      } finally {
        Storage.prototype.getItem = original;
      }
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
