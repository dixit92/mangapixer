import { vi } from 'vitest';
import { WritableSignal, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatBottomSheetRef, MAT_BOTTOM_SHEET_DATA } from '@angular/material/bottom-sheet';

import {
  ReaderSettingsMenuComponent, ReaderOptionsSheetComponent, ReaderOptionsHost,
  ReaderView, ViewPref, FitMode, ReadingDirection,
} from './reader-settings-menu.component';
import { ReaderPreferencesService } from '../../core/reading/reader-preferences.service';
import { WebtoonNavPreferencesService } from './webtoon-nav.service';

describe('ReaderSettingsMenuComponent', () => {
  function create() {
    TestBed.configureTestingModule({
      imports: [ReaderSettingsMenuComponent],
      providers: [provideNoopAnimations()],
    });
    localStorage.clear();
    localStorage.setItem('mangapixer-reader-upscaler', 'smooth'); // pre-1.25.0 flows: a device that chose Smooth (the default is Crisp since 1.25.0)
    const fixture = TestBed.createComponent(ReaderSettingsMenuComponent);
    fixture.detectChanges();
    return { fixture, c: fixture.componentInstance, prefs: TestBed.inject(ReaderPreferencesService) };
  }

  it('renders a page-transition trigger button', () => {
    const { fixture } = create();
    const btn = (fixture.nativeElement as HTMLElement).querySelector('button[aria-label="Page transition"]');
    expect(btn).toBeTruthy();
  });

  it('choose() writes the preference through the shared service', () => {
    const { c, prefs } = create();
    c.choose('reveal');
    expect(prefs.pageAnimation()).toBe('reveal');
    c.choose('none');
    expect(prefs.pageAnimation()).toBe('none');
  });

  /**
   * 1.19.0 image scaling. A second trigger, present in every view, opens a menu
   * with two radio groups: Upscaling (how an upscaled page is resampled) and
   * Page quality (how many pixels are fetched). Its tooltip is the WebGPU status
   * readout - the only way to confirm the GPU path on a real device.
   */
  describe('Upscaling menu (1.19.0)', () => {
    function openRendering(fixture: ReturnType<typeof create>['fixture']) {
      const trigger = (fixture.nativeElement as HTMLElement)
        .querySelector('button[aria-label="Upscaling"]') as HTMLElement;
      expect(trigger).toBeTruthy();
      trigger.click();
      fixture.detectChanges();
      return { trigger, panel: document.querySelector('.reader-options-menu') as HTMLElement };
    }

    it('renders a Upscaling trigger in the paged AND webtoon views', () => {
      const { fixture } = create();
      const host = fixture.nativeElement as HTMLElement;
      expect(host.querySelector('button[aria-label="Upscaling"]')).toBeTruthy();
      fixture.componentRef.setInput('view', 'webtoon');
      fixture.detectChanges();
      expect(host.querySelector('button[aria-label="Upscaling"]')).toBeTruthy();
    });

    /** Upscaling items by their option label (the aria-label may carry an engine / reason line). */
    const renderingItems = (panel: HTMLElement) => Array.from(panel.querySelectorAll<HTMLButtonElement>('button[mat-menu-item]'))
      .filter((i) => (i.getAttribute('aria-label') ?? '').startsWith('Upscaling: '));
    const renderingItem = (panel: HTMLElement, label: string) =>
      renderingItems(panel).find((i) => new RegExp(`^Upscaling: ${label}( - |$)`).test(i.getAttribute('aria-label') ?? ''))!;
    const noteOf = (item: HTMLElement) => item.querySelector('.option-note')?.textContent?.trim() ?? null;
    const webgl = (floatTargets = true) => ({ status: 'ready' as const, floatTargets, maxTextureSize: 8192 });

    it('offers Smooth / Crisp / Enhance and Auto / Full as menuitemradios, defaults highlighted', () => {
      const { fixture } = create();
      const { panel } = openRendering(fixture);
      const rendering = renderingItems(panel);
      expect(rendering.map((i) => (i.getAttribute('aria-label') ?? '').replace(/ - .*$/, '')))
        .toEqual(['Upscaling: Smooth', 'Upscaling: Crisp', 'Upscaling: Enhance']);
      const quality = Array.from(panel.querySelectorAll<HTMLElement>('button[mat-menu-item]'))
        .filter((i) => (i.getAttribute('aria-label') ?? '').startsWith('Page quality'));
      // textContent carries the icon ligature first, as elsewhere in these menus.
      expect(quality.map((i) => (i.textContent ?? '').trim().split(/\s+/).pop())).toEqual(['Auto', 'Full']);
      const items = [...rendering, ...quality];
      for (const i of items) expect(i.getAttribute('role')).toBe('menuitemradio');
      expect(renderingItem(panel, 'Smooth').classList.contains('selected-option')).toBe(true);
      expect(renderingItem(panel, 'Smooth').getAttribute('aria-checked')).toBe('true');
      expect(quality[0].classList.contains('selected-option')).toBe(true);
      expect(renderingItem(panel, 'Enhance').getAttribute('aria-checked')).toBe('false');
      expect(quality[1].getAttribute('aria-checked')).toBe('false');
      // Colour highlight, never a tick - same rule as every other reader menu.
      for (const i of items) expect(i.querySelector('mat-icon')?.textContent?.trim()).not.toBe('check');
      // The selected option names its engine on a second line.
      expect(noteOf(renderingItem(panel, 'Smooth'))).toBe('Browser scaling');
    });

    it('page quality is selectable and persists through the shared service', () => {
      const { fixture, prefs } = create();
      const { panel } = openRendering(fixture);
      const full = Array.from(panel.querySelectorAll<HTMLElement>('button[mat-menu-item]'))
        .find((i) => (i.textContent ?? '').trim().endsWith('Full'))!;
      full.click();
      expect(prefs.pageQuality()).toBe('full');
    });

    it('without WebGPU or WebGL2, Crisp and Enhance are disabled, each with its reason', () => {
      const { fixture, c } = create();
      expect(c.support.support()).toBe('unavailable'); // jsdom: no navigator.gpu
      expect(c.support.webgl().status).toBe('unsupported'); // ... and no WebGL2
      expect(c.enhanceDisabled()).toBe(true);
      const { panel } = openRendering(fixture);
      const sharp = renderingItem(panel, 'Crisp');
      const enhance = renderingItem(panel, 'Enhance');
      expect(sharp.disabled).toBe(true);
      expect(enhance.disabled).toBe(true);
      expect(noteOf(sharp)).toBe('Needs WebGL2, which this browser lacks');
      expect(noteOf(enhance)).toBe('Needs WebGL2, which this browser lacks');
      expect(sharp.getAttribute('aria-label')).toBe('Upscaling: Crisp - Needs WebGL2, which this browser lacks');
      expect(panel.querySelector('.menu-hint')?.textContent).toContain('WebGPU unavailable, no WebGL2');
    });

    it('over plain HTTP with WebGL2: Enhance runs on WebGL2 and says why; Crisp is WebGL2', () => {
      const { fixture, c, prefs } = create();
      c.support.secure.set(false);
      c.support.webgl.set(webgl());
      prefs.setUpscaler('enhance');
      fixture.detectChanges();
      const { panel } = openRendering(fixture);
      const enhance = renderingItem(panel, 'Enhance');
      expect(enhance.disabled).toBe(false);
      expect(enhance.classList.contains('selected-option')).toBe(true);
      expect(noteOf(enhance)).toBe('Anime4K (WebGL2)');
      expect(noteOf(renderingItem(panel, 'Crisp'))).toBeNull(); // not selected: no line
      expect(renderingItem(panel, 'Crisp').disabled).toBe(false);
      expect(c.renderingHint()).toBe('GPU: WebGPU needs HTTPS, WebGL2 ready');  // desktop: engine on the option line
      renderingItem(panel, 'Crisp').click();
      fixture.detectChanges();
      expect(prefs.upscaler()).toBe('sharp');
      expect(c.renderingHint()).toBe('GPU: WebGPU needs HTTPS, WebGL2 ready');  // desktop: engine on the option line
    });

    it('over plain HTTP without float render targets, Enhance is disabled: needs a secure connection', () => {
      const { fixture, c, prefs } = create();
      c.support.secure.set(false);
      c.support.webgl.set(webgl(false));
      prefs.setUpscaler('enhance'); // saved on another connection
      fixture.detectChanges();
      const { panel } = openRendering(fixture);
      const enhance = renderingItem(panel, 'Enhance');
      expect(enhance.disabled).toBe(true);
      expect(noteOf(enhance)).toBe('Needs a secure connection (HTTPS)');
      // The page shows Smooth, so that is what is highlighted; the stored choice is kept.
      expect(renderingItem(panel, 'Smooth').classList.contains('selected-option')).toBe(true);
      expect(enhance.getAttribute('aria-checked')).toBe('false');
      expect(prefs.upscaler()).toBe('enhance');
    });

    it('chooseUpscaler refuses a choice that cannot run here, but always allows Smooth', () => {
      const { c, prefs } = create();
      c.chooseUpscaler('enhance');
      expect(prefs.upscaler()).toBe('smooth'); // rejected: no WebGPU here
      c.chooseUpscaler('sharp');
      expect(prefs.upscaler()).toBe('smooth'); // rejected: no WebGL2 here
      c.chooseUpscaler('smooth');
      expect(prefs.upscaler()).toBe('smooth');
    });

    it('while WebGPU is still being probed, Enhance waits (disabled, "Checking WebGPU…")', () => {
      const { fixture, c } = create();
      c.support.support.set('checking');
      c.support.webgl.set(webgl());
      fixture.detectChanges();
      const { panel } = openRendering(fixture);
      expect(renderingItem(panel, 'Enhance').disabled).toBe(true);
      expect(noteOf(renderingItem(panel, 'Enhance'))).toBe('Checking WebGPU…');
      expect(renderingItem(panel, 'Crisp').disabled).toBe(false);
    });

    it('with WebGPU ready, Enhance is selectable in every view, webtoon included (1.24.0)', () => {
      const { fixture, c, prefs } = create();
      c.support.support.set('ready');
      fixture.detectChanges();
      expect(c.enhanceDisabled()).toBe(false);
      expect(c.renderingHint()).toBe('GPU: WebGPU ready, no WebGL2');
      c.chooseUpscaler('enhance');
      expect(prefs.upscaler()).toBe('enhance');
      expect(c.renderingHint()).toBe('GPU: WebGPU ready, no WebGL2');  // desktop: engine on the option line

      fixture.componentRef.setInput('view', 'webtoon');
      fixture.detectChanges();
      expect(c.enhanceDisabled()).toBe(false);
      const { panel } = openRendering(fixture);
      const enhance = renderingItem(panel, 'Enhance');
      expect(enhance.disabled).toBe(false);
      expect(noteOf(enhance)).toBe('Anime4K');
    });

    /** 1.24.0 owner decision: M ("Efficient") by default, VL as "Max quality". */
    describe('Enhance quality group (1.24.0)', () => {
      const qualityItems = (panel: HTMLElement) => Array.from(panel.querySelectorAll<HTMLButtonElement>('button[mat-menu-item]'))
        .filter((i) => (i.getAttribute('aria-label') ?? '').startsWith('Enhance quality'));

      /** 1.25.0: the group is a sub-choice of Enhance, shown only while Enhance runs. */
      function createEnhance() {
        const created = create();
        created.c.support.support.set('ready');
        created.prefs.setUpscaler('enhance');
        created.fixture.detectChanges();
        return created;
      }

      it('offers Efficient (default, highlighted) and Max quality as menuitemradios', () => {
        const { fixture } = createEnhance();
        const items = qualityItems(openRendering(fixture).panel);
        expect(items.map((i) => i.getAttribute('aria-label'))).toEqual(['Enhance quality: Efficient', 'Enhance quality: Max quality']);
        for (const i of items) expect(i.getAttribute('role')).toBe('menuitemradio');
        expect(items[0].classList.contains('selected-option')).toBe(true);
        expect(items[0].getAttribute('aria-checked')).toBe('true');
        expect(items[1].getAttribute('aria-checked')).toBe('false');
      });

      it('is shown only while Enhance is the Upscaling on screen (1.25.0)', () => {
        const { fixture, c, prefs } = create();
        c.support.support.set('ready');
        fixture.detectChanges();
        expect(c.showEnhanceQuality()).toBe(false); // Smooth
        expect(qualityItems(openRendering(fixture).panel)).toEqual([]);
        prefs.setUpscaler('enhance');
        expect(c.showEnhanceQuality()).toBe(true);
        c.support.support.set('unavailable'); // saved Enhance cannot run: Smooth shows
        expect(c.showEnhanceQuality()).toBe(false);
      });

      it('chooseEnhanceQuality refuses a change while Enhance cannot run', () => {
        const { c, prefs } = create();
        c.chooseEnhanceQuality('max');
        expect(prefs.enhanceQuality()).toBe('balanced');
      });

      it('with WebGPU ready, Max quality persists through the shared service', () => {
        const { fixture, prefs } = createEnhance();
        const items = qualityItems(openRendering(fixture).panel);
        items[1].click();
        expect(prefs.enhanceQuality()).toBe('max');
        expect(localStorage.getItem(ReaderPreferencesService.EnhanceQualityKey)).toBe('max');
      });

      it('on WebGL2, Max quality is disabled and Efficient runs; the stored Max is kept (1.25.0)', () => {
        const { fixture, c, prefs } = create();
        c.support.secure.set(false);
        c.support.webgl.set(webgl());
        prefs.setUpscaler('enhance');
        prefs.setEnhanceQuality('max');
        fixture.detectChanges();
        const items = qualityItems(openRendering(fixture).panel);
        expect(items[1].disabled).toBe(true);
        expect(items[0].disabled).toBe(false);
        expect(items[0].getAttribute('aria-checked')).toBe('true');
        expect(c.qualityHint()).toBe('Max quality needs WebGPU, which needs HTTPS; Efficient runs here');
        c.chooseEnhanceQuality('max');
        expect(prefs.enhanceQuality()).toBe('max'); // untouched: WebGPU would run it
      });

      it('hints what each choice costs', () => {
        const { c, prefs } = create();
        expect(c.qualityHint()).toBe('Lighter on GPU memory and battery');
        prefs.setEnhanceQuality('max');
        expect(c.qualityHint()).toContain('more GPU memory and battery');
      });

      it('is hidden in the vertical view (always Efficient there); the stored choice is untouched', () => {
        const { fixture, c, prefs } = createEnhance();
        prefs.setEnhanceQuality('max');
        fixture.componentRef.setInput('view', 'webtoon');
        fixture.detectChanges();
        expect(c.showEnhanceQuality()).toBe(false);
        const panel = openRendering(fixture).panel;
        expect(qualityItems(panel)).toEqual([]);
        expect(panel.querySelector('[role="group"][aria-label="Enhance quality"]')).toBeNull();
        expect(panel.textContent).not.toContain('Enhance quality');
        expect(panel.textContent).not.toContain('Max quality');
        // Upscaling and Page quality are still offered in vertical.
        expect(panel.querySelector('[role="group"][aria-label="Upscaling"]')).not.toBeNull();
        expect(panel.querySelector('[role="group"][aria-label="Page quality"]')).not.toBeNull();
        expect(prefs.enhanceQuality()).toBe('max');
        // Back in a paged view the group returns (with the stored choice).
        fixture.componentRef.setInput('view', 'spread');
        fixture.detectChanges();
        expect(c.showEnhanceQuality()).toBe(true);
      });
    });

    it('five taps on the status line toggle the GPU timing readout, without closing the menu', () => {
      const { fixture, c, prefs } = create();
      c.support.support.set('ready');
      prefs.setUpscaler('enhance');
      fixture.detectChanges();
      const { panel } = openRendering(fixture);
      const status = panel.querySelector('button.hint-tap') as HTMLButtonElement;
      expect(status.textContent?.trim()).toBe('GPU: WebGPU ready, no WebGL2');  // desktop: engine on the option line
      for (let i = 0; i < 5; i++) status.click();
      fixture.detectChanges();
      expect(c.support.statsVisible()).toBe(true);
      expect(document.querySelector('.reader-options-menu')).toBeTruthy(); // still open
      c.support.recordTiming('band', 18);
      c.support.recordTiming('band', 22);
      c.support.recordTiming('band', 400); // median, so one slow band does not skew it
      fixture.detectChanges();
      expect(status.textContent?.trim()).toBe('GPU: WebGPU ready, no WebGL2 - 22 ms/band');
      for (let i = 0; i < 5; i++) status.click();
      expect(c.support.statsVisible()).toBe(false);
    });

    it('the GPU status readout is a short, owner-verifiable string', () => {
      const { c, prefs } = create();
      expect(c.support.statusText()).toBe('GPU: WebGPU unavailable, no WebGL2');
      c.support.support.set('ready');
      expect(c.support.statusText()).toBe('GPU: WebGPU ready, no WebGL2');
      c.support.webgl.set(webgl(false));
      expect(c.support.statusText()).toBe('GPU: WebGPU ready, WebGL2 ready (Crisp only)');
      c.support.secure.set(false);
      c.support.support.set('unavailable');
      c.support.webgl.set(webgl());
      expect(c.support.statusText()).toBe('GPU: WebGPU needs HTTPS, WebGL2 ready');
      prefs.setUpscaler('enhance');
      expect(c.support.statusText()).toBe('GPU: Enhance - Anime4K (WebGL2)');
      c.support.secure.set(true);
      expect(c.support.statusText()).toBe('GPU: Enhance - Anime4K (WebGL2)');
      c.support.support.set('checking');
      expect(c.support.statusText()).toContain('checking');
    });
  });

  /**
   * 1.20.0 "Downscale filter": a third radio group in the same Upscaling menu,
   * after Page quality. Only meaningful for a sized (Auto) page request, so it
   * is offered disabled with a reason under Full - the same treatment as
   * Enhance being disabled with a reason on webtoon.
   */
  describe('Downscale filter menu (1.20.0)', () => {
    function openRendering(fixture: ReturnType<typeof create>['fixture']) {
      const trigger = (fixture.nativeElement as HTMLElement)
        .querySelector('button[aria-label="Upscaling"]') as HTMLElement;
      trigger.click();
      fixture.detectChanges();
      return { panel: document.querySelector('.reader-options-menu') as HTMLElement };
    }

    it('offers Sharp / Balanced / Soft as menuitemradios, Balanced selected by default', () => {
      const { fixture } = create();
      const { panel } = openRendering(fixture);
      const items = Array.from(panel.querySelectorAll<HTMLElement>('button[aria-label^="Downscale filter:"]'));
      expect(items.map((i) => i.getAttribute('aria-label'))).toEqual([
        'Downscale filter: Sharp', 'Downscale filter: Balanced', 'Downscale filter: Soft',
      ]);
      for (const i of items) expect(i.getAttribute('role')).toBe('menuitemradio');
      const byLabel = (l: string) => items.find((i) => i.getAttribute('aria-label') === `Downscale filter: ${l}`)!;
      expect(byLabel('Balanced').classList.contains('selected-option')).toBe(true);
      expect(byLabel('Balanced').getAttribute('aria-checked')).toBe('true');
      expect(byLabel('Sharp').getAttribute('aria-checked')).toBe('false');
      expect(byLabel('Soft').getAttribute('aria-checked')).toBe('false');
    });

    it('choosing a filter persists through the shared service', () => {
      const { fixture, prefs } = create();
      const { panel } = openRendering(fixture);
      (panel.querySelector('button[aria-label="Downscale filter: Sharp"]') as HTMLElement).click();
      expect(prefs.downscaleFilter()).toBe('sharp');
    });

    it('is disabled with "Applies to Auto page quality" when Page quality is Full', () => {
      const { fixture, c, prefs } = create();
      prefs.setPageQuality('full');
      fixture.detectChanges();
      expect(c.filterDisabled()).toBe(true);
      const { panel } = openRendering(fixture);
      const sharp = panel.querySelector('button[aria-label="Downscale filter: Sharp"]') as HTMLButtonElement;
      expect(sharp.disabled).toBe(true);
      const hints = Array.from(panel.querySelectorAll('.menu-hint')).map((e) => e.textContent);
      expect(hints.some((t) => t?.includes('Applies to Auto page quality'))).toBe(true);
    });

    it('re-enables once Page quality goes back to Auto', () => {
      const { fixture, c, prefs } = create();
      prefs.setPageQuality('full');
      fixture.detectChanges();
      expect(c.filterDisabled()).toBe(true);
      prefs.setPageQuality('auto');
      fixture.detectChanges();
      expect(c.filterDisabled()).toBe(false);
    });

    it('chooseDownscaleFilter refuses to change the preference while disabled', () => {
      const { c, prefs } = create();
      prefs.setPageQuality('full');
      c.chooseDownscaleFilter('sharp');
      expect(prefs.downscaleFilter()).toBe('balanced');
    });
  });

  it('emits opened/closed for chrome pinning', () => {
    const { c } = create();
    let opened = 0;
    let closed = 0;
    c.opened.subscribe(() => opened++);
    c.closed.subscribe(() => closed++);
    c.opened.emit();
    c.closed.emit();
    expect(opened).toBe(1);
    expect(closed).toBe(1);
  });

  /**
   * 1.10.0 (F4): the active transition is marked with the accent highlight
   * (`selected-option` + aria-checked on a menuitemradio) and keeps its own
   * glyph; the checkmark is gone. The panel renders in a CDK overlay, so open it
   * and query `document`.
   */
  it('marks the active transition with the colour highlight, not a tick', () => {
    const { fixture, c } = create();
    c.choose('reveal');
    fixture.detectChanges();
    ((fixture.nativeElement as HTMLElement).querySelector('button[aria-label="Page transition"]') as HTMLElement).click();
    fixture.detectChanges();
    const panel = document.querySelector('.reader-options-menu') as HTMLElement;
    expect(panel).not.toBeNull();
    const items = Array.from(panel.querySelectorAll<HTMLElement>('button[mat-menu-item]'));
    expect(items.map((i) => i.getAttribute('role'))).toEqual(['menuitemradio', 'menuitemradio', 'menuitemradio']);
    const byLabel = (l: string) => items.find((i) => (i.textContent ?? '').trim().endsWith(l))!;
    expect(byLabel('Reveal').classList.contains('selected-option')).toBe(true);
    expect(byLabel('Reveal').getAttribute('aria-checked')).toBe('true');
    expect(byLabel('Reveal').querySelector('mat-icon')?.textContent?.trim()).toBe('gradient');
    expect(byLabel('Slide').classList.contains('selected-option')).toBe(false);
    expect(byLabel('Slide').getAttribute('aria-checked')).toBe('false');
    for (const i of items) expect(i.querySelector('mat-icon')?.textContent?.trim()).not.toBe('check');
  });

  /**
   * 1.11.0: in the webtoon view the same toolbar slot offers the tap-to-scroll
   * step instead of a page transition (which is meaningless while scrolling).
   * One trigger per view, never two; the active step carries the same highlight.
   */
  it('webtoon view: the slot becomes Tap to scroll (Off / 80 / 90 / 100), highlighted like the transition menu', () => {
    const { fixture, c } = create();
    fixture.componentRef.setInput('view', 'webtoon');
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('button[aria-label="Page transition"]')).toBeNull();
    const trigger = host.querySelector('button[aria-label="Tap to scroll"]') as HTMLElement;
    expect(trigger).toBeTruthy();
    trigger.click();
    fixture.detectChanges();
    const panel = Array.from(document.querySelectorAll<HTMLElement>('.reader-options-menu')).at(-1)!;
    const items = Array.from(panel.querySelectorAll<HTMLElement>('button[mat-menu-item]'));
    expect(items.map((i) => i.getAttribute('aria-label')))
      .toEqual(['Tap to scroll: Off', 'Tap to scroll: 80%', 'Tap to scroll: 90%', 'Tap to scroll: 100%']);
    expect(items.map((i) => i.getAttribute('aria-checked'))).toEqual(['false', 'false', 'true', 'false']); // default 90
    expect(items[2].classList.contains('selected-option')).toBe(true);
    c.chooseTapStep(100);
    fixture.detectChanges();
    expect(TestBed.inject(WebtoonNavPreferencesService).tapStep()).toBe(100);
    expect(items[3].getAttribute('aria-checked')).toBe('true');
  });
});

/**
 * Phone options sheet (1.10.0, F2). Rendered directly with a fake host (the
 * reader's live state as signals + spied actions) and a fake sheet ref, so the
 * tests cover the sheet's own contract: grouped radio chips with exactly one
 * checked per group, the effective-layout mapping, action dispatch, the webtoon
 * variant, and the close-then-navigate rule for chapter/help.
 */
describe('ReaderOptionsSheetComponent', () => {
  function makeHost(overrides: Partial<{
    view: ReaderView; viewPref: ViewPref | null; shifted: boolean; fit: FitMode; dir: ReadingDirection;
    prev: boolean; next: boolean; narrow: boolean;
  }> = {}) {
    const o = { view: 'paged' as ReaderView, viewPref: null as ViewPref | null, shifted: true, fit: 'screen' as FitMode,
      dir: 'ltr' as ReadingDirection, prev: false, next: true, narrow: false, ...overrides };
    const host: ReaderOptionsHost = {
      view: signal(o.view),
      viewPref: signal(o.viewPref),
      spreadShifted: signal(o.shifted),
      narrowPortrait: signal(o.narrow),
      fitMode: signal(o.fit),
      direction: signal(o.dir),
      webtoonWidthPct: signal(70),
      hasPrevChapter: signal(o.prev),
      hasNextChapter: signal(o.next),
      prevNeighbor: signal(o.prev ? { displayName: 'Ch 0' } : null),
      nextNeighbor: signal(o.next ? { displayName: 'Ch 2' } : null),
      chooseView: vi.fn(),
      chooseSpread: vi.fn(),
      setFitMode: vi.fn(),
      setDirection: vi.fn(),
      setWebtoonWidth: vi.fn(),
      prevChapter: vi.fn(),
      nextChapter: vi.fn(),
      toggleHelp: vi.fn(),
    };
    return host;
  }

  function create(host: ReaderOptionsHost = makeHost()) {
    const ref = { dismiss: vi.fn() };
    // Reset first so a single test may call create() several times (the
    // activeLayout test creates the sheet once per layout scenario).
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [ReaderOptionsSheetComponent],
      providers: [
        provideNoopAnimations(),
        { provide: MAT_BOTTOM_SHEET_DATA, useValue: host },
        { provide: MatBottomSheetRef, useValue: ref },
      ],
    });
    localStorage.clear();
    localStorage.setItem('mangapixer-reader-upscaler', 'smooth'); // pre-1.25.0 flows: a device that chose Smooth (the default is Crisp since 1.25.0)
    const fixture = TestBed.createComponent(ReaderOptionsSheetComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const group = (id: string) => el.querySelector(`[aria-labelledby="${id}"]`) as HTMLElement;
    const chips = (id: string) => Array.from(group(id).querySelectorAll<HTMLElement>('button[role="radio"]'));
    const checked = (id: string) => chips(id).filter((c) => c.getAttribute('aria-checked') === 'true');
    const chip = (id: string, label: string) => chips(id).find((c) => (c.textContent ?? '').trim().endsWith(label))!;
    return { fixture, c: fixture.componentInstance, el, host, ref, group, chips, checked, chip };
  }

  it('paged: four labelled radio groups, each with exactly one checked + highlighted chip', () => {
    const { checked, chips } = create();
    for (const id of ['reader-options-layout', 'reader-options-fit', 'reader-options-direction', 'reader-options-transition']) {
      expect(chips(id).length, id).toBeGreaterThan(1);
      const on = checked(id);
      expect(on.length, `${id} has one checked chip`).toBe(1);
      expect(on[0].classList.contains('selected'), `${id} checked chip is highlighted`).toBe(true);
      // Every chip keeps its own glyph; the selected-state is colour, never a tick.
      for (const c of chips(id)) expect(c.querySelector('mat-icon')?.textContent?.trim()).not.toBe('check');
    }
    expect(checked('reader-options-layout')[0].textContent).toContain('Single page');
    expect(checked('reader-options-fit')[0].textContent).toContain('Fit screen');
    expect(checked('reader-options-direction')[0].textContent).toContain('Left to right');
    expect(checked('reader-options-transition')[0].textContent).toContain('Slide');
  });

  it('activeLayout is the EFFECTIVE layout: auto pref wins, spread splits by the current spread shift, webtoon by view', () => {
    expect(create(makeHost({ view: 'paged', viewPref: 'auto' })).c.activeLayout()).toBe('auto');
    expect(create(makeHost({ view: 'spread', viewPref: 'auto' })).c.activeLayout()).toBe('auto');
    expect(create(makeHost({ view: 'spread', viewPref: 'spread', shifted: true })).c.activeLayout()).toBe('spread-shifted');
    expect(create(makeHost({ view: 'spread', viewPref: null, shifted: false })).c.activeLayout()).toBe('spread');
    expect(create(makeHost({ view: 'paged', viewPref: null })).c.activeLayout()).toBe('paged');
    expect(create(makeHost({ view: 'webtoon', viewPref: 'spread' })).c.activeLayout()).toBe('webtoon');
  });

  it('layout chips dispatch to the reader exactly like the desktop menu items', () => {
    const { host, chip } = create();
    chip('reader-options-layout', 'Auto').click();
    expect(host.chooseView).toHaveBeenLastCalledWith('auto');
    chip('reader-options-layout', 'Single page').click();
    expect(host.chooseView).toHaveBeenLastCalledWith('paged');
    chip('reader-options-layout', 'Double page').click();
    expect(host.chooseSpread).toHaveBeenLastCalledWith(false);
    chip('reader-options-layout', 'Double, shifted').click();
    expect(host.chooseSpread).toHaveBeenLastCalledWith(true);
    chip('reader-options-layout', 'Vertical').click();
    expect(host.chooseView).toHaveBeenLastCalledWith('webtoon');
  });

  it('fit / direction / transition chips apply immediately and keep the sheet open', () => {
    const { host, ref, chip } = create();
    chip('reader-options-fit', 'Fit width').click();
    expect(host.setFitMode).toHaveBeenCalledWith('width');
    chip('reader-options-direction', 'Right to left').click();
    expect(host.setDirection).toHaveBeenCalledWith('rtl');
    chip('reader-options-transition', 'Reveal').click();
    expect(TestBed.inject(ReaderPreferencesService).pageAnimation()).toBe('reveal');
    expect(ref.dismiss).not.toHaveBeenCalled();
  });

  it('the highlight follows the live reader state (signals), e.g. after a fit change', () => {
    const host = makeHost();
    const { fixture, checked } = create(host);
    (host.fitMode as ReturnType<typeof signal<FitMode>>).set('original');
    fixture.detectChanges();
    expect(checked('reader-options-fit')[0].textContent).toContain('Original size');
  });

  it('webtoon: paged-only groups are replaced by the page-width slider', () => {
    const { el, group } = create(makeHost({ view: 'webtoon' }));
    expect(group('reader-options-layout')).toBeTruthy();
    expect(el.querySelector('[aria-labelledby="reader-options-fit"]')).toBeNull();
    expect(el.querySelector('[aria-labelledby="reader-options-direction"]')).toBeNull();
    expect(el.querySelector('[aria-labelledby="reader-options-transition"]')).toBeNull();
    expect(el.querySelector('mat-slider.width-slider input')).toBeTruthy();
    expect(el.querySelector('#reader-options-width')?.textContent).toContain('70%');
  });

  it('webtoon: offers the Tap to scroll step (Off / 80 / 90 / 100, default 90) as a fifth radio group (1.11.0)', () => {
    const { chips, checked, chip } = create(makeHost({ view: 'webtoon' }));
    expect(chips('reader-options-tap').map((c) => (c.textContent ?? '').trim().replace(/^block/, '').trim()))
      .toEqual(['Off', '80%', '90%', '100%']);
    expect(checked('reader-options-tap')[0].textContent).toContain('90%');
    chip('reader-options-tap', 'Off').click();
    expect(TestBed.inject(WebtoonNavPreferencesService).tapStep()).toBe(0);
    expect(localStorage.getItem(WebtoonNavPreferencesService.TapStepKey)).toBe('0');
  });

  it('double page on a narrow portrait screen keeps the chip checked and explains the single-page render inline (1.11.0)', () => {
    const { el, checked } = create(makeHost({ view: 'spread', viewPref: 'spread', shifted: true, narrow: true }));
    expect(checked('reader-options-layout')[0].textContent).toContain('Double, shifted');
    expect(el.querySelector('.note')?.textContent).toContain('one page at a time');
    // No note when the screen is not narrow-portrait, or the layout is not double page.
    expect(create(makeHost({ view: 'spread', viewPref: 'spread', narrow: false })).el.querySelector('.note')).toBeNull();
    expect(create(makeHost({ view: 'paged', narrow: true })).el.querySelector('.note')).toBeNull();
  });

  it('chapter buttons reflect neighbour availability and close the sheet BEFORE navigating', () => {
    const { el, host, ref } = create(makeHost({ prev: false, next: true }));
    const buttons = Array.from(el.querySelectorAll<HTMLButtonElement>('.chapter-row button'));
    expect(buttons.length).toBe(2);
    const [prev, next] = buttons;
    expect(prev.disabled).toBe(true);
    expect(next.disabled).toBe(false);
    expect(next.getAttribute('aria-label')).toBe('Next archive: Ch 2');

    const order: string[] = [];
    ref.dismiss.mockImplementation(() => order.push('dismiss'));
    (host.nextChapter as ReturnType<typeof vi.fn>).mockImplementation(() => order.push('next'));
    next.click();
    expect(order).toEqual(['dismiss', 'next']);
  });

  /**
   * 1.19.0 image scaling on the phone: two more chip groups, shown in EVERY view
   * (Page quality applies to webtoon too), with Enhance disabled and explained
   * wherever it cannot work.
   */
  describe('Upscaling + Page quality chip groups (1.19.0)', () => {
    const webgl = (floatTargets = true) => ({ status: 'ready' as const, floatTargets, maxTextureSize: 8192 });

    it('adds both groups in the paged view, each with exactly one checked chip', () => {
      const { chips, checked } = create();
      expect(chips('reader-options-rendering').length).toBe(3); // Smooth / Crisp / Enhance (1.25.0)
      expect(chips('reader-options-quality').length).toBe(2);
      for (const id of ['reader-options-rendering', 'reader-options-quality']) {
        expect(checked(id).length, id).toBe(1);
        expect(checked(id)[0].classList.contains('selected'), id).toBe(true);
      }
      expect(checked('reader-options-rendering')[0].textContent).toContain('Smooth');
      expect(checked('reader-options-quality')[0].textContent).toContain('Auto');
    });

    it('keeps both groups in the webtoon view (page quality applies there too)', () => {
      const { el, chips } = create(makeHost({ view: 'webtoon' }));
      expect(el.querySelector('[aria-labelledby="reader-options-rendering"]')).toBeTruthy();
      expect(chips('reader-options-quality').length).toBe(2);
    });

    it('page quality chips apply immediately and keep the sheet open', () => {
      const { ref, chip } = create();
      chip('reader-options-quality', 'Full').click();
      expect(TestBed.inject(ReaderPreferencesService).pageQuality()).toBe('full');
      expect(ref.dismiss).not.toHaveBeenCalled();
    });

    it('Crisp and Enhance are disabled and explained without WebGPU or WebGL2 (jsdom has neither)', () => {
      const { c, el, chip } = create();
      expect(c.enhanceDisabled()).toBe(true);
      expect((chip('reader-options-rendering', 'Enhance') as HTMLButtonElement).disabled).toBe(true);
      expect((chip('reader-options-rendering', 'Crisp') as HTMLButtonElement).disabled).toBe(true);
      expect(el.querySelector('.rendering-notes')?.textContent?.trim())
        .toBe('Crisp: Needs WebGL2, which this browser lacks · Enhance: Needs WebGL2, which this browser lacks');
      c.pickUpscaler('enhance');
      c.pickUpscaler('sharp');
      expect(TestBed.inject(ReaderPreferencesService).upscaler()).toBe('smooth');
    });

    it('over plain HTTP with WebGL2: Crisp and Enhance are selectable, the status line names the engine', () => {
      const { fixture, c, el, checked, chip } = create();
      c.support.secure.set(false);
      c.support.webgl.set(webgl());
      fixture.detectChanges();
      expect(el.querySelector('.rendering-notes')).toBeNull(); // nothing disabled
      chip('reader-options-rendering', 'Enhance').click();
      fixture.detectChanges();
      expect(TestBed.inject(ReaderPreferencesService).upscaler()).toBe('enhance');
      expect(checked('reader-options-rendering')[0].textContent).toContain('Enhance');
      expect(el.querySelector('.hint-tap')?.textContent?.trim()).toBe('GPU: Enhance - Anime4K (WebGL2)');
      chip('reader-options-rendering', 'Crisp').click();
      fixture.detectChanges();
      expect(el.querySelector('.hint-tap')?.textContent?.trim()).toBe('GPU: Crisp - AMD FSR 1');
    });

    it('without float render targets over HTTP, only Enhance is disabled: needs a secure connection', () => {
      const { fixture, c, el, chip } = create();
      c.support.secure.set(false);
      c.support.webgl.set(webgl(false));
      fixture.detectChanges();
      expect((chip('reader-options-rendering', 'Crisp') as HTMLButtonElement).disabled).toBe(false);
      expect((chip('reader-options-rendering', 'Enhance') as HTMLButtonElement).disabled).toBe(true);
      expect(el.querySelector('.rendering-notes')?.textContent?.trim()).toBe('Enhance: Needs a secure connection (HTTPS)');
    });

    it('with WebGPU ready the Enhance chip is selectable, in webtoon too (1.24.0)', () => {
      const { fixture, c, checked, chip } = create();
      c.support.support.set('ready');
      fixture.detectChanges();
      expect(c.enhanceDisabled()).toBe(false);
      chip('reader-options-rendering', 'Enhance').click();
      fixture.detectChanges();
      expect(TestBed.inject(ReaderPreferencesService).upscaler()).toBe('enhance');
      expect(checked('reader-options-rendering')[0].textContent).toContain('Enhance');

      const webtoon = create(makeHost({ view: 'webtoon' }));
      webtoon.c.support.support.set('ready');
      webtoon.fixture.detectChanges();
      expect(webtoon.c.enhanceDisabled()).toBe(false);
      expect(webtoon.c.renderingHint()).toBe('GPU: WebGPU ready, no WebGL2'); // fresh device prefs: Smooth
      expect((webtoon.chip('reader-options-rendering', 'Enhance') as HTMLButtonElement).disabled).toBe(false);
    });

    it('adds an Enhance quality chip group while Enhance runs: Efficient by default, Max quality persists', () => {
      const { fixture, c, el, chips, checked, chip } = create();
      expect(el.querySelector('#reader-options-enhance-quality')).toBeNull(); // Smooth: no sub-choice
      c.support.support.set('ready');
      TestBed.inject(ReaderPreferencesService).setUpscaler('enhance');
      fixture.detectChanges();
      expect(chips('reader-options-enhance-quality').length).toBe(2);
      expect(checked('reader-options-enhance-quality')[0].textContent).toContain('Efficient');
      chip('reader-options-enhance-quality', 'Max quality').click();
      fixture.detectChanges();
      expect(TestBed.inject(ReaderPreferencesService).enhanceQuality()).toBe('max');
      expect(checked('reader-options-enhance-quality')[0].textContent).toContain('Max quality');
    });

    it('on WebGL2 the Max quality chip is disabled and Efficient is checked (1.25.0)', () => {
      const { fixture, c, checked, chip } = create();
      c.support.secure.set(false);
      c.support.webgl.set(webgl());
      const prefs = TestBed.inject(ReaderPreferencesService);
      prefs.setUpscaler('enhance');
      prefs.setEnhanceQuality('max');
      fixture.detectChanges();
      expect((chip('reader-options-enhance-quality', 'Max quality') as HTMLButtonElement).disabled).toBe(true);
      expect(checked('reader-options-enhance-quality')[0].textContent).toContain('Efficient');
      expect(c.qualityHint()).toBe('Max quality needs WebGPU, which needs HTTPS; Efficient runs here');
    });

    it('hides the Enhance quality group in the vertical view and shows it again in paged', () => {
      const { fixture, c, el, host, chips, checked } = create(makeHost({ view: 'webtoon' }));
      const prefs = TestBed.inject(ReaderPreferencesService);
      c.support.support.set('ready');
      prefs.setUpscaler('enhance');
      prefs.setEnhanceQuality('max');
      fixture.detectChanges();
      expect(el.querySelector('#reader-options-enhance-quality')).toBeNull();
      expect(el.querySelector('[aria-labelledby="reader-options-enhance-quality"]')).toBeNull();
      expect(el.textContent).not.toContain('Max quality');
      expect(chips('reader-options-rendering').length).toBe(3);
      expect(chips('reader-options-quality').length).toBeGreaterThan(0);
      expect(prefs.enhanceQuality()).toBe('max');
      (host.view as WritableSignal<ReaderView>).set('paged');
      fixture.detectChanges();
      expect(chips('reader-options-enhance-quality').length).toBe(2);
      expect(checked('reader-options-enhance-quality')[0].textContent).toContain('Max quality');
    });
  });

  /**
   * 1.20.0 "Downscale filter" on the phone: a third chip radiogroup
   * (`reader-options-filter`), shown in every view like Page quality, disabled
   * with a reason under Full page quality.
   */
  describe('Downscale filter chip group (1.20.0)', () => {
    it('adds the group with exactly one checked chip, Efficient by default', () => {
      const { chips, checked } = create();
      expect(chips('reader-options-filter').map((c) => (c.textContent ?? '').trim()).length).toBe(3);
      expect(checked('reader-options-filter').length).toBe(1);
      expect(checked('reader-options-filter')[0].textContent).toContain('Balanced');
    });

    it('keeps the group in the webtoon view too', () => {
      const { el, chips } = create(makeHost({ view: 'webtoon' }));
      expect(el.querySelector('[aria-labelledby="reader-options-filter"]')).toBeTruthy();
      expect(chips('reader-options-filter').length).toBe(3);
    });

    it('choosing a chip persists through the shared service and keeps the sheet open', () => {
      const { ref, chip } = create();
      chip('reader-options-filter', 'Sharp').click();
      expect(TestBed.inject(ReaderPreferencesService).downscaleFilter()).toBe('sharp');
      expect(ref.dismiss).not.toHaveBeenCalled();
    });

    it('is disabled with "Applies to Auto page quality" once Page quality is Full', () => {
      const { fixture, c, el, chip } = create();
      TestBed.inject(ReaderPreferencesService).setPageQuality('full');
      fixture.detectChanges();
      expect(c.filterDisabled()).toBe(true);
      expect((chip('reader-options-filter', 'Sharp') as HTMLButtonElement).disabled).toBe(true);
      const filterGroup = el.querySelector('[aria-labelledby="reader-options-filter"]')!.parentElement!;
      expect(filterGroup.querySelector('.group-hint')?.textContent).toContain('Applies to Auto page quality');
    });

    it('pickDownscaleFilter refuses to change the preference while disabled', () => {
      const { c } = create();
      TestBed.inject(ReaderPreferencesService).setPageQuality('full');
      c.pickDownscaleFilter('sharp');
      expect(TestBed.inject(ReaderPreferencesService).downscaleFilter()).toBe('balanced');
    });
  });

  it('Reading help and Close dismiss the sheet (help then opens the overlay)', () => {
    const { el, host, ref } = create();
    (el.querySelector('.help-row') as HTMLElement).click();
    expect(ref.dismiss).toHaveBeenCalledTimes(1);
    expect(host.toggleHelp).toHaveBeenCalledTimes(1);
    (el.querySelector('button[aria-label="Close reader options"]') as HTMLElement).click();
    expect(ref.dismiss).toHaveBeenCalledTimes(2);
  });
});
