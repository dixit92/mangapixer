import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Observable, Subject, of, throwError } from 'rxjs';

import { RatingStarsComponent, RatingStarsMode } from './rating-stars.component';
import { RatingChange, RatingsStateService } from './ratings-state.service';

/** Host that drives the control through its signal inputs, as the Info sheet / cards do. */
@Component({
  standalone: true,
  imports: [RatingStarsComponent],
  template: `<div class="cover"><app-rating-stars [nodeId]="nodeId()" [rating]="rating()" [mode]="mode()" [disabled]="disabled()" /></div>`,
})
class HostComponent {
  readonly nodeId = signal('node-1');
  readonly rating = signal<number | null | undefined>(null);
  readonly mode = signal<RatingStarsMode>('edit');
  readonly disabled = signal(false);
}

/** Fake state service: records persistence calls; a test can hold a save open (`pending`) or fail it. */
class FakeRatingsStateService {
  readonly changedSubject = new Subject<RatingChange>();
  readonly changed$ = this.changedSubject.asObservable();
  pending: Subject<void> | null = null;
  fail = false;
  setRating = vi.fn((nodeId: string, score: number | null): Observable<void> => {
    if (this.fail) return throwError(() => new Error('nope'));
    if (this.pending) return this.pending.asObservable();
    this.changedSubject.next({ nodeId, score });
    return of(undefined);
  });
}

/**
 * 1.52.0 lane R: the rating control - 5 stars with halves over a stored 1-10 score. Halves by tap / click, the slider's keyboard and ARIA
 * (`aria-valuetext` "3.5 of 5 stars"), "Clear rating", optimistic saves with revert, only the latest score sent while a save is in
 * flight, cross-instance sync, and the read-only stars / badge.
 */
describe('RatingStarsComponent', () => {
  let fake: FakeRatingsStateService;

  function create(rating: number | null | undefined = null, mode: RatingStarsMode = 'edit') {
    fake = new FakeRatingsStateService();
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideNoopAnimations(), { provide: RatingsStateService, useValue: fake }],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.rating.set(rating);
    fixture.componentInstance.mode.set(mode);
    fixture.detectChanges();
    return fixture;
  }

  const el = (f: ReturnType<typeof create>) => f.nativeElement as HTMLElement;
  const slider = (f: ReturnType<typeof create>) => el(f).querySelector<HTMLElement>('[role="slider"]')!;
  const icons = (f: ReturnType<typeof create>) =>
    [...el(f).querySelectorAll('.star mat-icon')].map((i) => i.textContent!.trim());
  const half = (f: ReturnType<typeof create>, score: number) => el(f).querySelector<HTMLElement>(`[data-testid="rating-half-${score}"]`)!;
  const key = (f: ReturnType<typeof create>, k: string) => {
    const event = new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true });
    slider(f).dispatchEvent(event);
    f.detectChanges();
    return event;
  };
  const clearButton = (f: ReturnType<typeof create>) => el(f).querySelector<HTMLButtonElement>('[data-testid="rating-clear"]');

  describe('edit', () => {
    it('is one focusable slider of 1-10 named by its value in stars', () => {
      const f = create(7);
      const s = slider(f);
      expect(s.getAttribute('tabindex')).toBe('0');
      expect(s.getAttribute('aria-valuemin')).toBe('1');
      expect(s.getAttribute('aria-valuemax')).toBe('10');
      expect(s.getAttribute('aria-valuenow')).toBe('7');
      expect(s.getAttribute('aria-valuetext')).toBe('3.5 of 5 stars');
      expect(s.getAttribute('aria-label')).toBe('Your rating');
      // The halves are pointer targets only - no extra tab stops inside the slider.
      expect(s.querySelectorAll('button, [tabindex]').length).toBe(0);
    });

    it('shows score / 2 stars: full, half and empty', () => {
      expect(icons(create(7))).toEqual(['star', 'star', 'star', 'star_half', 'star_border']);
      TestBed.resetTestingModule();
      expect(icons(create(10))).toEqual(['star', 'star', 'star', 'star', 'star']);
      TestBed.resetTestingModule();
      expect(icons(create(1))).toEqual(['star_half', 'star_border', 'star_border', 'star_border', 'star_border']);
    });

    it('without a rating reads "Not rated", has no aria-valuenow and no Clear button', () => {
      const f = create(null);
      expect(slider(f).hasAttribute('aria-valuenow')).toBe(false);
      expect(slider(f).getAttribute('aria-valuetext')).toBe('Not rated');
      expect(icons(f).every((i) => i === 'star_border')).toBe(true);
      expect(clearButton(f)).toBeNull();
    });

    it('a click on the left half sets the half star, on the right half the whole star', () => {
      const f = create(null);
      half(f, 7).click();
      f.detectChanges();
      expect(fake.setRating).toHaveBeenLastCalledWith('node-1', 7);
      expect(slider(f).getAttribute('aria-valuetext')).toBe('3.5 of 5 stars');
      half(f, 2).click();
      f.detectChanges();
      expect(fake.setRating).toHaveBeenLastCalledWith('node-1', 2);
      expect(icons(f)).toEqual(['star', 'star_border', 'star_border', 'star_border', 'star_border']);
    });

    it('a click does not reach a card link around it', () => {
      const f = create(null);
      const outer = vi.fn();
      el(f).querySelector('.cover')!.addEventListener('click', outer);
      half(f, 4).click();
      expect(outer).not.toHaveBeenCalled();
    });

    it('a click on the score it already has sends nothing', () => {
      const f = create(6);
      half(f, 6).click();
      expect(fake.setRating).not.toHaveBeenCalled();
    });

    it('arrows step by a half star, Page keys by a whole star, Home / End go to the ends', () => {
      const f = create(6);
      key(f, 'ArrowRight');
      expect(slider(f).getAttribute('aria-valuenow')).toBe('7');
      key(f, 'ArrowUp');
      expect(slider(f).getAttribute('aria-valuenow')).toBe('8');
      key(f, 'ArrowLeft');
      key(f, 'ArrowDown');
      expect(slider(f).getAttribute('aria-valuenow')).toBe('6');
      key(f, 'PageUp');
      expect(slider(f).getAttribute('aria-valuenow')).toBe('8');
      key(f, 'PageDown');
      expect(slider(f).getAttribute('aria-valuenow')).toBe('6');
      key(f, 'End');
      expect(slider(f).getAttribute('aria-valuetext')).toBe('5 of 5 stars');
      key(f, 'Home');
      expect(slider(f).getAttribute('aria-valuetext')).toBe('0.5 of 5 stars');
      expect(fake.setRating).toHaveBeenLastCalledWith('node-1', 1);
    });

    it('the keys stop at the ends: no step below half a star or above five', () => {
      const f = create(1);
      key(f, 'ArrowLeft');
      expect(slider(f).getAttribute('aria-valuenow')).toBe('1');
      key(f, 'End');
      key(f, 'ArrowRight');
      key(f, 'PageUp');
      expect(slider(f).getAttribute('aria-valuenow')).toBe('10');
    });

    it('from no rating, a step up starts at half a star', () => {
      const f = create(null);
      key(f, 'ArrowRight');
      expect(slider(f).getAttribute('aria-valuenow')).toBe('1');
    });

    it('Delete and Backspace clear', () => {
      const f = create(8);
      expect(key(f, 'Delete').defaultPrevented).toBe(true);
      expect(fake.setRating).toHaveBeenLastCalledWith('node-1', null);
      expect(slider(f).getAttribute('aria-valuetext')).toBe('Not rated');
      TestBed.resetTestingModule();
      const g = create(3);
      key(g, 'Backspace');
      expect(fake.setRating).toHaveBeenLastCalledWith('node-1', null);
    });

    it('other keys are left alone (Tab moves on)', () => {
      const f = create(4);
      expect(key(f, 'Tab').defaultPrevented).toBe(false);
      expect(key(f, 'a').defaultPrevented).toBe(false);
      expect(fake.setRating).not.toHaveBeenCalled();
    });

    it('"Clear rating" is a visible button that clears', () => {
      const f = create(5);
      const button = clearButton(f)!;
      expect(button.textContent!.trim()).toBe('Clear rating');
      button.click();
      f.detectChanges();
      expect(fake.setRating).toHaveBeenCalledWith('node-1', null);
      expect(clearButton(f)).toBeNull();
    });

    it('reverts to the stored rating when the save fails', () => {
      const f = create(4);
      fake.fail = true;
      half(f, 9).click();
      f.detectChanges();
      expect(slider(f).getAttribute('aria-valuenow')).toBe('4');
    });

    it('while a save is in flight, only the latest wanted score is sent next', () => {
      const f = create(null);
      fake.pending = new Subject<void>();
      key(f, 'ArrowRight'); // 1: sent
      key(f, 'ArrowRight'); // 2: queued
      key(f, 'ArrowRight'); // 3: replaces 2
      expect(slider(f).getAttribute('aria-valuenow')).toBe('3');
      expect(fake.setRating).toHaveBeenCalledTimes(1);
      expect(fake.setRating).toHaveBeenLastCalledWith('node-1', 1);
      const first = fake.pending;
      fake.pending = null;
      first.next();
      first.complete();
      expect(fake.setRating).toHaveBeenCalledTimes(2);
      expect(fake.setRating).toHaveBeenLastCalledWith('node-1', 3);
    });

    it('does nothing while disabled', () => {
      const f = create(4);
      f.componentInstance.disabled.set(true);
      f.detectChanges();
      half(f, 8).click();
      key(f, 'ArrowRight');
      expect(fake.setRating).not.toHaveBeenCalled();
      expect(slider(f).getAttribute('aria-disabled')).toBe('true');
    });

    it('follows the server value when the host re-fetches', () => {
      const f = create(4);
      f.componentInstance.rating.set(9);
      f.detectChanges();
      expect(slider(f).getAttribute('aria-valuenow')).toBe('9');
    });

    it('follows a rating set elsewhere for the same node, and ignores other nodes', () => {
      const f = create(4);
      fake.changedSubject.next({ nodeId: 'other', score: 10 });
      f.detectChanges();
      expect(slider(f).getAttribute('aria-valuenow')).toBe('4');
      fake.changedSubject.next({ nodeId: 'node-1', score: 2 });
      f.detectChanges();
      expect(slider(f).getAttribute('aria-valuenow')).toBe('2');
      fake.changedSubject.next({ nodeId: 'node-1', score: null });
      f.detectChanges();
      expect(slider(f).hasAttribute('aria-valuenow')).toBe(false);
    });

    it('treats a value outside 1-10 from the server as not rated', () => {
      expect(slider(create(0)).getAttribute('aria-valuetext')).toBe('Not rated');
      TestBed.resetTestingModule();
      expect(slider(create(11)).getAttribute('aria-valuetext')).toBe('Not rated');
      TestBed.resetTestingModule();
      expect(slider(create(undefined)).getAttribute('aria-valuetext')).toBe('Not rated');
    });
  });

  describe('stars (read-only)', () => {
    it('shows the five stars as one image named "Rated 3.5 of 5", with no slider and no button', () => {
      const f = create(7, 'stars');
      const img = el(f).querySelector('[role="img"]')!;
      expect(img.getAttribute('aria-label')).toBe('Rated 3.5 of 5');
      expect(el(f).querySelector('[role="slider"]')).toBeNull();
      expect(el(f).querySelector('button')).toBeNull();
      expect([...img.querySelectorAll('mat-icon')].map((i) => i.textContent!.trim()))
        .toEqual(['star', 'star', 'star', 'star_half', 'star_border']);
    });

    it('shows nothing when not rated', () => {
      expect(el(create(null, 'stars')).querySelector('[role="img"]')).toBeNull();
    });
  });

  describe('badge', () => {
    it('is one star and the number, named "Rated 4.5 of 5"', () => {
      const f = create(9, 'badge');
      const badge = el(f).querySelector('[data-testid="rating-badge"]')!;
      expect(badge.textContent!.replace('star', '').trim()).toBe('4.5');
      expect(badge.getAttribute('aria-label')).toBe('Rated 4.5 of 5');
      expect(badge.getAttribute('role')).toBe('img');
    });

    it('whole stars have no decimals', () => {
      expect(el(create(8, 'badge')).querySelector('[data-testid="rating-badge"]')!.textContent!.replace('star', '').trim()).toBe('4');
    });

    it('is absent when not rated (unrated cards show nothing)', () => {
      expect(el(create(null, 'badge')).querySelector('[data-testid="rating-badge"]')).toBeNull();
    });

    it('appears when the same node is rated elsewhere (the Info sheet), and goes when cleared', () => {
      const f = create(null, 'badge');
      fake.changedSubject.next({ nodeId: 'node-1', score: 6 });
      f.detectChanges();
      expect(el(f).querySelector('[data-testid="rating-badge"]')!.textContent).toContain('3');
      fake.changedSubject.next({ nodeId: 'node-1', score: null });
      f.detectChanges();
      expect(el(f).querySelector('[data-testid="rating-badge"]')).toBeNull();
    });
  });
});
