import { TestBed } from '@angular/core/testing';

import { INSTALL_HINT_ENV, InstallHintEnv, InstallHintService } from './install-hint.service';

describe('InstallHintService', () => {
  const env: InstallHintEnv & { apple: boolean; standalone: boolean } = {
    apple: true,
    standalone: false,
    isAppleTouch() { return this.apple; },
    isStandalone() { return this.standalone; },
  };

  function create(): InstallHintService {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: INSTALL_HINT_ENV, useValue: env }] });
    return TestBed.inject(InstallHintService);
  }

  beforeEach(() => {
    localStorage.clear();
    env.apple = true;
    env.standalone = false;
  });
  afterEach(() => vi.restoreAllMocks());

  describe('first reader open', () => {
    it('shows once on Apple touch in a browser tab, remembering it per device', () => {
      const svc = create();
      svc.onReaderOpened();
      expect(svc.visible()).toBe(true);
      expect(svc.reason()).toBe('reader-open');

      // A later page load on the same device does not repeat it.
      const later = create();
      later.onReaderOpened();
      expect(later.visible()).toBe(false);
    });

    it('does not re-show for further chapters in the same page load', () => {
      const svc = create();
      svc.onReaderOpened();
      svc.hide();
      svc.onReaderOpened();
      expect(svc.visible()).toBe(false);
    });

    it('never shows off Apple touch, and does not burn the first-open flag there', () => {
      env.apple = false;
      const svc = create();
      svc.onReaderOpened();
      expect(svc.visible()).toBe(false);
      expect(localStorage.getItem(InstallHintService.ReaderOpenSeenKey)).toBeNull();
    });

    it('never shows when already running standalone', () => {
      env.standalone = true;
      const svc = create();
      svc.onReaderOpened();
      svc.onFullscreenRequested();
      expect(svc.visible()).toBe(false);
    });
  });

  describe('fullscreen tap', () => {
    it('shows every time until dismissed, even after the first-open hint was seen', () => {
      const svc = create();
      svc.onReaderOpened();
      svc.hide();
      svc.onFullscreenRequested();
      expect(svc.visible()).toBe(true);
      expect(svc.reason()).toBe('fullscreen');
      svc.hide();
      svc.onFullscreenRequested();
      expect(svc.visible()).toBe(true);
    });

    it('never shows off Apple touch', () => {
      env.apple = false;
      const svc = create();
      svc.onFullscreenRequested();
      expect(svc.visible()).toBe(false);
    });
  });

  describe('dismissal', () => {
    it('"Not now" silences this page load only', () => {
      const svc = create();
      svc.onFullscreenRequested();
      svc.dismissForSession();
      expect(svc.visible()).toBe(false);
      svc.onFullscreenRequested();
      expect(svc.visible()).toBe(false);
      expect(localStorage.getItem(InstallHintService.DismissedKey)).toBeNull();

      // Reload: the fullscreen hint is back.
      const reloaded = create();
      reloaded.onFullscreenRequested();
      expect(reloaded.visible()).toBe(true);
    });

    it('"Don\'t show again" persists across reloads for both triggers', () => {
      const svc = create();
      svc.onFullscreenRequested();
      svc.dismissForever();
      expect(svc.visible()).toBe(false);
      expect(localStorage.getItem(InstallHintService.DismissedKey)).toBe('1');

      const reloaded = create();
      reloaded.onFullscreenRequested();
      expect(reloaded.visible()).toBe(false);
      reloaded.onReaderOpened();
      expect(reloaded.visible()).toBe(false);
    });
  });

  describe('storage unavailable', () => {
    beforeEach(() => {
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('denied'); });
      vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('denied'); });
    });

    it('still shows the hint and honours both dismissals in memory', () => {
      const svc = create();
      svc.onReaderOpened();
      expect(svc.visible()).toBe(true);
      svc.dismissForSession();
      svc.onFullscreenRequested();
      expect(svc.visible()).toBe(false);

      const other = create();
      other.onFullscreenRequested();
      expect(other.visible()).toBe(true);
      other.dismissForever();
      other.onFullscreenRequested();
      expect(other.visible()).toBe(false);
    });
  });
});
