import { ComponentFixture, TestBed } from '@angular/core/testing';

import { VolumeStackSummaryDto } from '../../core/api/api-types';
import { MissingChapterCardComponent } from './missing-chapter-card.component';
import { VolumeIncompleteBadgeComponent } from './volume-incomplete-badge.component';

/** The incomplete mark and the missing-chapter placeholder of the Volumes view (1.29.0). */
describe('volume stack pieces', () => {
  function summary(over: Partial<VolumeStackSummaryDto> = {}): VolumeStackSummaryDto {
    return { key: '3', label: 'Volume 3', presentCount: 8, chapterCount: 9, missingCount: 1, extraCount: 0, hasVolumeArchive: false, confidence: 'Exact', ...over };
  }

  describe('VolumeIncompleteBadgeComponent', () => {
    function render(s: VolumeStackSummaryDto): HTMLElement {
      const fixture: ComponentFixture<VolumeIncompleteBadgeComponent> = TestBed.createComponent(VolumeIncompleteBadgeComponent);
      fixture.componentRef.setInput('summary', s);
      fixture.detectChanges();
      return fixture.nativeElement as HTMLElement;
    }

    it('shows whole chapters present of whole chapters held, with an accessible label', () => {
      const badge = render(summary()).querySelector('[data-testid="stack-incomplete"]')!;
      expect(badge.textContent?.trim()).toBe('8/9');
      expect(badge.getAttribute('aria-label')).toBe('8 of 9 chapters');
    });

    it('never counts an extra as a present chapter', () => {
      // 9 members = 8 whole chapters + the extra 45.5; 10 chapters held, 2 missing.
      const badge = render(summary({ presentCount: 9, extraCount: 1, chapterCount: 10, missingCount: 2 })).querySelector('[data-testid="stack-incomplete"]')!;
      expect(badge.textContent?.trim()).toBe('8/10');
    });

    it('is absent when nothing is missing', () => {
      expect(render(summary({ missingCount: 0 })).querySelector('[data-testid="stack-incomplete"]')).toBeNull();
    });

    it('falls back to present + missing when the volume\'s chapter count is unknown', () => {
      const badge = render(summary({ chapterCount: null, presentCount: 5, missingCount: 2 })).querySelector('[data-testid="stack-incomplete"]')!;
      expect(badge.textContent?.trim()).toBe('5/7');
    });
  });

  describe('MissingChapterCardComponent', () => {
    function render(chapter: string): HTMLElement {
      const fixture = TestBed.createComponent(MissingChapterCardComponent);
      fixture.componentRef.setInput('chapter', chapter);
      fixture.detectChanges();
      return fixture.nativeElement as HTMLElement;
    }

    it('shows "Ch. N" and "Missing" and names itself "Chapter N, missing"', () => {
      const el = render('39');
      const card = el.querySelector('[data-testid="missing-chapter"]')!;
      expect(card.textContent).toContain('Ch. 39');
      expect(card.textContent).toContain('Missing');
      expect(card.getAttribute('aria-label')).toBe('Chapter 39, missing');
      expect(card.getAttribute('title')).toBe('Not in your library');
    });

    it('is not clickable or focusable: no link, no button, no tab stop', () => {
      const el = render('39');
      expect(el.querySelector('a, button, [tabindex]')).toBeNull();
    });
  });
});
