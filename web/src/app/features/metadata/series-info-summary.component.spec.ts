import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { SeriesInfoDto } from '../../core/api/api-types';
import { SeriesInfoSummaryComponent } from './series-info-summary.component';
import { seriesInfo } from './series-info.testing';

@Component({
  standalone: true,
  imports: [SeriesInfoSummaryComponent],
  template: `<app-series-info-summary [info]="info()" [compact]="compact()" />`,
})
class HostComponent {
  readonly info = signal<SeriesInfoDto>(seriesInfo());
  readonly compact = signal(false);
}

/** Shared presentational summary (1.24.0): every state, folding, and text-only binding. */
describe('SeriesInfoSummaryComponent', () => {
  function render(info: SeriesInfoDto, compact = false) {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.info.set(info);
    fixture.componentInstance.compact.set(compact);
    fixture.detectChanges();
    return fixture;
  }

  const text = (f: ReturnType<typeof render>) => (f.nativeElement as HTMLElement).textContent ?? '';
  const q = (f: ReturnType<typeof render>, sel: string) => (f.nativeElement as HTMLElement).querySelector(sel);

  it('shows the no-information state', () => {
    const f = render(seriesInfo({ state: 'None', title: null }));
    expect(q(f, '[data-testid="series-none"]')).not.toBeNull();
    expect(q(f, '[data-testid="series-title"]')).toBeNull();
  });

  it('explains a Don\'t match node', () => {
    const f = render(seriesInfo({ state: 'DontMatch', title: null }));
    expect(q(f, '[data-testid="series-dont-match"]')!.textContent).toContain("Don't match");
  });

  it('1.34.0: shows a collection as "Collection about" its series, with the note and without numbers', () => {
    const f = render(seriesInfo({ state: 'CollectionAbout', title: 'Starlight Academy', description: 'About the series.' }));
    const el = f.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="series-collection"]')!.textContent).toContain('Collection about');
    expect(el.querySelector('[data-testid="series-title"]')!.textContent).toContain('Starlight Academy');
    expect(el.querySelector('[data-testid="series-collection-note"]')!.textContent).toContain('its items are matched on their own');
  });

  it('a series is not a collection', () => {
    const f = render(seriesInfo({ state: 'Web' }));
    expect((f.nativeElement as HTMLElement).querySelector('[data-testid="series-collection"]')).toBeNull();
  });

  it('lists the series of a mixed folder with counts', () => {
    const f = render(seriesInfo({ state: 'Mixed', title: null, mixedSeries: [{ name: 'Alpha', count: 3 }, { name: 'Beta', count: 1 }] }));
    expect(q(f, '[data-testid="series-mixed"]')).not.toBeNull();
    expect(text(f)).toContain('Alpha');
    expect(text(f)).toContain('3 items');
    expect(text(f)).toContain('1 item');
  });

  it('renders title, facts, credits and genres', () => {
    const f = render(seriesInfo({
      state: 'Web',
      title: 'Web Title',
      origin: 'Japan',
      startYear: 1999,
      creators: [{ name: 'A Writer', role: 'writer' }, { name: 'An Artist', role: 'artist' }],
      genres: ['Action', 'Drama'],
      licensedEn: true,
    }));
    expect(q(f, '[data-testid="series-title"]')!.textContent).toBe('Web Title');
    expect(text(f)).toContain('Japan · 1999');
    expect(text(f)).toContain('Story: A Writer');
    expect(text(f)).toContain('Art: An Artist');
    expect(text(f)).toContain('Licensed in English');
    // Genres are plain text (chips would look clickable before faceted search exists).
    expect(q(f, '[data-testid="series-genres"]')!.textContent!.trim()).toBe('Action · Drama');
    expect(f.nativeElement.querySelector('.chip')).toBeNull();
  });

  it('shows a short alternative-title line when the full list is shown elsewhere (series page)', () => {
    TestBed.configureTestingModule({ imports: [SeriesInfoSummaryComponent], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(SeriesInfoSummaryComponent);
    fixture.componentRef.setInput('info', seriesInfo({ altTitles: ['A', 'B', 'C', 'D', 'E'] }));
    fixture.componentRef.setInput('shortAltTitles', true);
    fixture.detectChanges();
    const alt = (fixture.nativeElement as HTMLElement).querySelector('.alt')!.textContent!;
    expect(alt).toContain('A, B');
    expect(alt).toContain('+3');
    expect(alt).not.toContain('C');
  });

  it('folds alternative titles and genres in compact mode', () => {
    const f = render(seriesInfo({ altTitles: ['A', 'B', 'C', 'D'], genres: ['g1', 'g2', 'g3', 'g4', 'g5'] }), true);
    expect(q(f, '.alt')!.textContent).toContain('A, B');
    expect(q(f, '.alt')!.textContent).toContain('+2');
    expect(q(f, '[data-testid="series-genres"]')!.textContent!.replace(/\s+/g, ' ').trim()).toBe('g1 · g2 · g3 · +2 more');
  });

  it('clamps the description in compact mode with a More toggle', () => {
    const f = render(seriesInfo({ description: 'Long text' }), true);
    const desc = q(f, '.description')!;
    expect(desc.classList.contains('clamped')).toBe(true);
    (q(f, '.more') as HTMLButtonElement).click();
    f.detectChanges();
    expect(desc.classList.contains('clamped')).toBe(false);
    expect(q(f, '.more')!.textContent).toContain('Less');
  });

  it('binds provider and ComicInfo text as text, never markup', () => {
    const f = render(seriesInfo({ description: '<img src=x onerror="alert(1)"><b>bold</b>' }));
    expect(q(f, '.description img')).toBeNull();
    expect(q(f, '.description b')).toBeNull();
    expect(q(f, '.description')!.textContent).toContain('<b>bold</b>');
  });

  it('shows the per-item ComicInfo block for an archive', () => {
    const f = render(seriesInfo({ nodeKind: 'Archive', item: { number: '12', volume: 3, title: 'Issue Twelve', summary: 'An issue.' } }));
    const item = q(f, '[data-testid="series-item"]')!;
    expect(item.textContent).toContain('Vol 3 · #12');
    expect(item.textContent).toContain('Issue Twelve');
    expect(item.textContent).toContain('An issue.');
  });

  it('omits the description when the host shows it elsewhere (series page About)', () => {
    TestBed.configureTestingModule({ imports: [SeriesInfoSummaryComponent] });
    const f = TestBed.createComponent(SeriesInfoSummaryComponent);
    f.componentRef.setInput('info', seriesInfo({ description: 'Shown once.' }));
    f.componentRef.setInput('showDescription', false);
    f.detectChanges();
    expect((f.nativeElement as HTMLElement).querySelector('.description')).toBeNull();
  });
  it('compact: "More" expands in place, or links to the series page when a moreLink is given (hover, 1.27.0)', () => {
    TestBed.configureTestingModule({ imports: [SeriesInfoSummaryComponent], providers: [provideRouter([])] });
    const f = TestBed.createComponent(SeriesInfoSummaryComponent);
    f.componentRef.setInput('info', seriesInfo({ description: 'A long description.' }));
    f.componentRef.setInput('compact', true);
    f.detectChanges();
    const el = f.nativeElement as HTMLElement;
    expect(el.querySelector('button.more')!.textContent).toBe('More');
    expect(el.querySelector('[data-testid="series-more-link"]')).toBeNull();

    f.componentRef.setInput('moreLink', ['/series', 'node42']);
    f.detectChanges();
    const link = el.querySelector('[data-testid="series-more-link"]') as HTMLAnchorElement;
    expect(link.textContent).toBe('More');
    expect(link.getAttribute('href')).toBe('/series/node42');
    expect(el.querySelector('button.more')).toBeNull();
  });
});
