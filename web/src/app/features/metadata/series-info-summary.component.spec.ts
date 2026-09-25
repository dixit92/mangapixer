import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

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
    expect(f.nativeElement.querySelectorAll('.chip').length).toBe(2);
  });

  it('folds alternative titles and genres in compact mode', () => {
    const f = render(seriesInfo({ altTitles: ['A', 'B', 'C', 'D'], genres: ['g1', 'g2', 'g3', 'g4', 'g5'] }), true);
    expect(q(f, '.alt')!.textContent).toContain('A, B');
    expect(q(f, '.alt')!.textContent).toContain('+2');
    expect(q(f, '.chip.more')!.textContent).toContain('+2');
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
});
