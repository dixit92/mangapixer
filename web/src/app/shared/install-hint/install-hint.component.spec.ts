import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { InstallHintComponent } from './install-hint.component';
import { INSTALL_HINT_ENV, InstallHintService } from './install-hint.service';

describe('InstallHintComponent', () => {
  function setup(apple = true) {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [InstallHintComponent],
      providers: [
        provideRouter([{ path: '**', children: [] }]),
        { provide: INSTALL_HINT_ENV, useValue: { isAppleTouch: () => apple, isStandalone: () => false } },
      ],
    });
    const fixture = TestBed.createComponent(InstallHintComponent);
    fixture.detectChanges();
    const svc = TestBed.inject(InstallHintService);
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, svc, el };
  }

  it('renders nothing until the service raises the hint', () => {
    const { el } = setup();
    expect(el.querySelector('.hint')).toBeNull();
  });

  it('shows the three steps as an accessible region with both dismiss buttons', () => {
    const { fixture, svc, el } = setup();
    svc.onReaderOpened();
    fixture.detectChanges();
    const card = el.querySelector('.hint') as HTMLElement;
    expect(card.getAttribute('role')).toBe('region');
    expect(el.querySelector('#' + card.getAttribute('aria-labelledby'))?.textContent).toContain('full screen');
    expect(card.querySelectorAll('ol li').length).toBe(3);
    expect(card.textContent).toContain('Share');
    expect(card.textContent).toContain('Add to Home Screen');
    const buttons = Array.from(card.querySelectorAll('button')).map((b) => b.textContent!.trim());
    expect(buttons).toEqual(['Not now', "Don't show again"]);
    // The unprompted first-open hint does not steal focus.
    expect(document.activeElement).not.toBe(card);
  });

  it('moves focus to the card on a Fullscreen tap', () => {
    const { fixture, svc, el } = setup();
    svc.onFullscreenRequested();
    fixture.detectChanges();
    fixture.detectChanges();
    expect(document.activeElement).toBe(el.querySelector('.hint'));
  });

  it('"Not now" hides it for the session only', () => {
    const { fixture, svc, el } = setup();
    svc.onFullscreenRequested();
    fixture.detectChanges();
    (el.querySelectorAll('button')[0] as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.querySelector('.hint')).toBeNull();
    expect(localStorage.getItem(InstallHintService.DismissedKey)).toBeNull();

    // Same page load: still session-dismissed.
    svc.onFullscreenRequested();
    expect(svc.visible()).toBe(false);
  });

  it('"Don\'t show again" removes the card and stores the flag', () => {
    const { fixture, svc, el } = setup();
    svc.onFullscreenRequested();
    fixture.detectChanges();
    (el.querySelectorAll('button')[1] as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.querySelector('.hint')).toBeNull();
    expect(localStorage.getItem(InstallHintService.DismissedKey)).toBe('1');
  });

  it('Escape dismisses for the session', () => {
    const { fixture, svc, el } = setup();
    svc.onFullscreenRequested();
    fixture.detectChanges();
    el.querySelector('.hint')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    expect(el.querySelector('.hint')).toBeNull();
  });

  it('hides on navigation without recording a choice', async () => {
    const { fixture, svc, el } = setup();
    svc.onFullscreenRequested();
    fixture.detectChanges();
    await TestBed.inject(Router).navigateByUrl('/elsewhere');
    fixture.detectChanges();
    expect(el.querySelector('.hint')).toBeNull();
    svc.onFullscreenRequested();
    expect(svc.visible()).toBe(true);
  });

  it('stays hidden off Apple touch', () => {
    const { fixture, svc, el } = setup(false);
    svc.onReaderOpened();
    svc.onFullscreenRequested();
    fixture.detectChanges();
    expect(el.querySelector('.hint')).toBeNull();
  });
});
