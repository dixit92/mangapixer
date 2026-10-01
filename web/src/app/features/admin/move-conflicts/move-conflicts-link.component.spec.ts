import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { MoveConflictsLinkComponent } from './move-conflicts-link.component';
import { MoveConflictsApiService } from './move-conflicts-api.service';

describe('MoveConflictsLinkComponent (1.31.0)', () => {
  function render(count: () => ReturnType<MoveConflictsApiService['count']>) {
    TestBed.configureTestingModule({
      imports: [MoveConflictsLinkComponent],
      providers: [provideRouter([]), { provide: MoveConflictsApiService, useValue: { count: vi.fn().mockImplementation(count) } }],
    });
    const fixture = TestBed.createComponent(MoveConflictsLinkComponent);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('links to the Move conflicts page with the open count while conflicts are open', () => {
    const el = render(() => of({ open: 3 }));
    const link = el.querySelector('[data-testid="move-conflicts-link"]')!;
    expect(link.textContent).toContain('Move conflicts (3)');
    expect(link.getAttribute('href')).toBe('/admin/move-conflicts');
  });

  it('shows nothing when no conflict is open, or the count cannot be read', () => {
    expect(render(() => of({ open: 0 })).querySelector('[data-testid="move-conflicts-link"]')).toBeNull();
    TestBed.resetTestingModule();
    expect(render(() => throwError(() => new Error('403'))).querySelector('[data-testid="move-conflicts-link"]')).toBeNull();
  });
});
