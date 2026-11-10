import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Subject, of, throwError } from 'rxjs';

import { ApiService } from '../../core/api/api.service';
import { NodeRatingComponent } from './node-rating.component';
import { RatingChange, RatingsStateService } from './ratings-state.service';

/** 1.52.0 lane R: "Your rating" of one node - read from the node, editable once known. */
describe('NodeRatingComponent', () => {
  let changes: Subject<RatingChange>;

  function create(getNode: ReturnType<typeof vi.fn>, known?: number | null) {
    changes = new Subject<RatingChange>();
    const ratings = { changed$: changes.asObservable(), setRating: vi.fn(() => of(undefined)) };
    TestBed.configureTestingModule({
      imports: [NodeRatingComponent],
      providers: [provideNoopAnimations(), { provide: ApiService, useValue: { getNode } }, { provide: RatingsStateService, useValue: ratings }],
    });
    const fixture = TestBed.createComponent(NodeRatingComponent);
    fixture.componentRef.setInput('nodeId', 'n1');
    fixture.componentRef.setInput('name', 'Akira');
    if (known !== undefined) fixture.componentRef.setInput('known', known);
    fixture.detectChanges();
    return { fixture, ratings, el: fixture.nativeElement as HTMLElement };
  }

  it('reads the viewer rating from the node and names the slider after it', () => {
    const getNode = vi.fn(() => of({ id: 'n1', myRating: 7 }));
    const { el } = create(getNode);
    expect(getNode).toHaveBeenCalledWith('n1');
    const slider = el.querySelector('[role="slider"]')!;
    expect(slider.getAttribute('aria-valuetext')).toBe('3.5 of 5 stars');
    expect(slider.getAttribute('aria-label')).toBe('Your rating: Akira');
    expect(slider.hasAttribute('aria-disabled')).toBe(false);
    expect(el.textContent).toContain('Your rating');
  });

  it('uses a known rating without reading the node', () => {
    const getNode = vi.fn();
    const { el } = create(getNode, 4);
    expect(getNode).not.toHaveBeenCalled();
    expect(el.querySelector('[role="slider"]')!.getAttribute('aria-valuenow')).toBe('4');
  });

  it('stays disabled when the node cannot be read (no write over an unknown value)', () => {
    const { el, ratings } = create(vi.fn(() => throwError(() => new Error('404'))));
    const slider = el.querySelector<HTMLElement>('[role="slider"]')!;
    expect(slider.getAttribute('aria-disabled')).toBe('true');
    el.querySelector<HTMLElement>('[data-testid="rating-half-6"]')!.click();
    expect(ratings.setRating).not.toHaveBeenCalled();
  });

  it('1.53.0 (audit N1): shows the server average as one star and a number under the stars, named for screen readers', () => {
    const { el } = create(vi.fn(() => of({ id: 'n1', myRating: 7, serverRating: { average: 8, count: 3 } })));
    const line = el.querySelector('[data-testid="server-rating"]')!;
    expect(line.getAttribute('role')).toBe('img');
    expect(line.getAttribute('aria-label')).toBe('Server average 4.0 of 5, 3 ratings');
    expect(line.textContent!.replace(/\s+/g, ' ').trim()).toBe('Server star 4.0 · 3 ratings');
    expect(line.querySelectorAll('mat-icon').length).toBe(1);
    // After the stars, inside the one-column block.
    const block = el.querySelector('[data-testid="node-rating"]')!;
    expect(block.lastElementChild).toBe(line);
  });

  it('shows no server line (and reserves no space) without an average', () => {
    const { el } = create(vi.fn(() => of({ id: 'n1', myRating: 7 })));
    expect(el.querySelector('[data-testid="server-rating"]')).toBeNull();
    expect(el.querySelector('[data-testid="node-rating"]')!.children.length).toBe(2);
  });

  it('reads the node again after the viewer rates it, so the average follows', () => {
    const getNode = vi.fn()
      .mockReturnValueOnce(of({ id: 'n1', myRating: null }))
      .mockReturnValueOnce(of({ id: 'n1', myRating: 6, serverRating: { average: 6, count: 1 } }));
    const { fixture, el } = create(getNode);
    expect(el.querySelector('[data-testid="server-rating"]')).toBeNull();
    changes.next({ nodeId: 'other', score: 4 });
    expect(getNode).toHaveBeenCalledTimes(1);
    changes.next({ nodeId: 'n1', score: 6 });
    fixture.detectChanges();
    expect(getNode).toHaveBeenCalledTimes(2);
    expect(el.querySelector('[data-testid="server-rating"]')!.getAttribute('aria-label')).toBe('Server average 3.0 of 5, 1 rating');
  });

  it('takes a known server average from the host with a known rating', () => {
    const getNode = vi.fn();
    TestBed.configureTestingModule({
      imports: [NodeRatingComponent],
      providers: [provideNoopAnimations(), { provide: ApiService, useValue: { getNode } },
        { provide: RatingsStateService, useValue: { changed$: of(), setRating: vi.fn() } }],
    });
    const fixture = TestBed.createComponent(NodeRatingComponent);
    fixture.componentRef.setInput('nodeId', 'n1');
    fixture.componentRef.setInput('known', 4);
    fixture.componentRef.setInput('knownServer', { average: 7.5, count: 2 });
    fixture.detectChanges();
    expect(getNode).not.toHaveBeenCalled();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="server-rating"]')!.textContent).toContain('3.8 · 2 ratings');
  });
});
