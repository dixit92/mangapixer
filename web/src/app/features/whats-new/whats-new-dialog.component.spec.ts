import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, provideRouter } from '@angular/router';

import { WhatsNewDialogComponent, WhatsNewDialogData } from './whats-new-dialog.component';

const TWO: WhatsNewDialogData = {
  current: '2.2.0',
  sections: [
    {
      version: '2.2.0', date: '2030-03-01',
      groups: [
        {
          name: 'Added',
          items: [
            {
              parts: [
                { text: 'Shared lanterns', bold: true },
                { text: ' are off until you turn them on in ' },
                { text: 'Administration', link: '/admin?tab=general' },
                { text: '. Search with ' },
                { text: 'lantern:', code: true },
                { text: ' - <b>not html</b>.' },
              ],
            },
          ],
        },
        { name: 'Fixed', items: [{ parts: [{ text: 'A fix.' }] }] },
      ],
    },
    { version: '2.1.0', date: null, groups: [{ name: 'Changed', items: [{ parts: [{ text: 'A change.' }] }] }] },
  ],
};

/**
 * 1.53.0 lane W: the What's new dialog renders the notes as text - versions newest first with their dates, groups as sub-headings,
 * bullets with bold / code / in-app links (a link closes the dialog and opens the page in the app), never HTML from the notes.
 */
describe('WhatsNewDialogComponent', () => {
  function create(data: WhatsNewDialogData) {
    const close = vi.fn();
    TestBed.configureTestingModule({
      imports: [WhatsNewDialogComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([{ path: '**', children: [] }]),
        { provide: MAT_DIALOG_DATA, useValue: data },
        { provide: MatDialogRef, useValue: { close } },
      ],
    });
    const fixture = TestBed.createComponent(WhatsNewDialogComponent);
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, close, router: TestBed.inject(Router) };
  }

  afterEach(() => TestBed.resetTestingModule());

  it('shows each version with its date, the groups and the bullets', () => {
    const { el } = create(TWO);

    expect(el.querySelector('[data-testid="whats-new-title"]')!.textContent!.trim()).toBe("What's new in MangaPixer 2.2.0");
    const sections = Array.from(el.querySelectorAll('[data-testid="whats-new-section"]'));
    expect(sections.map((s) => s.getAttribute('data-version'))).toEqual(['2.2.0', '2.1.0']);
    expect(sections[0].querySelector('.version')!.textContent!.replace(/\s+/g, ' ').trim()).toBe('2.2.0 · 2030-03-01');
    expect(sections[1].querySelector('.version')!.textContent!.trim()).toBe('2.1.0');
    expect(Array.from(sections[0].querySelectorAll('.group')).map((g) => g.textContent!.trim())).toEqual(['Added', 'Fixed']);
    expect(sections[0].querySelectorAll('li').length).toBe(2);
  });

  it('keeps bold, code and in-app links, and renders the rest as text', () => {
    const { el } = create(TWO);
    const li = el.querySelector('li')!;

    expect(li.querySelector('strong')!.textContent).toBe('Shared lanterns');
    expect(li.querySelector('code')!.textContent).toBe('lantern:');
    const link = li.querySelector('a')!;
    expect(link.textContent).toBe('Administration');
    expect(link.getAttribute('href')).toBe('/admin?tab=general');
    expect(li.querySelector('b')).toBeNull();
    expect(li.textContent!.replace(/\s+/g, ' ')).toContain('<b>not html</b>');
    expect(li.textContent!.replace(/\s+/g, ' ').trim())
      .toBe('Shared lanterns are off until you turn them on in Administration. Search with lantern: - <b>not html</b>.');
  });

  it('a link closes the dialog, then opens the page in the app', () => {
    const { el, close, router } = create(TWO);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    const click = new MouseEvent('click', { bubbles: true, cancelable: true });
    el.querySelector('a')!.dispatchEvent(click);

    expect(click.defaultPrevented).toBe(true);
    expect(close).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith('/admin?tab=general');
  });

  it('one version: no version heading, the date as a line', () => {
    const { el } = create({ current: '2.2.0', sections: [TWO.sections[0]] });

    expect(el.querySelector('.version')).toBeNull();
    expect(el.querySelector('[data-testid="whats-new-date"]')!.textContent!.trim()).toBe('Released 2030-03-01');
  });

  it('no notes for this version, and notes that could not be loaded, say so', () => {
    const { el } = create({ current: '2.2.0', sections: [] });
    expect(el.querySelector('[data-testid="whats-new-empty"]')).not.toBeNull();
    TestBed.resetTestingModule();

    const failed = create({ current: '', sections: [], error: true });
    expect(failed.el.querySelector('[data-testid="whats-new-error"]')).not.toBeNull();
    expect(failed.el.querySelector('[data-testid="whats-new-empty"]')).toBeNull();
  });

  it('"Got it" is the one action', () => {
    const { el } = create(TWO);
    const buttons = Array.from(el.querySelectorAll('button'));
    expect(buttons.map((b) => b.textContent!.trim())).toEqual(['Got it']);
  });
});
