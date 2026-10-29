import { TestBed } from '@angular/core/testing';

import { QuickResultsComponent } from './quick-results.component';
import { quickEntries, quickSections } from './quick-results.model';
import { SearchSuggestDto } from '../../../core/api/api-types';

/**
 * Archive context line in the quick results (1.50.0, lane W): an archive entry carries the server's `contextName` and the list shows it
 * (muted, one line) INSTEAD of the immediate parent folder's name; other entries are unchanged. Its own spec file (lane U owns the
 * quick-results specs).
 */
function answer(partial: Partial<SearchSuggestDto>): SearchSuggestDto {
  return { query: 'q', series: [], folders: [], archives: [], creators: [], tags: [], field: null, ...partial };
}

describe('quick results archive context line', () => {
  const sections = quickSections(answer({
    folders: [{ kind: 'folder', id: 'f1', label: 'Berserk', libraryId: 'L1', nodeKind: 'Folder', detail: 'Manga' }],
    archives: [
      { kind: 'archive', id: 'a1', label: 'Vol. 012', libraryId: 'L1', nodeKind: 'Archive', detail: 'Volumes', contextName: 'Berserk' },
      { kind: 'archive', id: 'a2', label: 'Loose', libraryId: 'L1', nodeKind: 'Archive', detail: 'Manga' },
    ],
  }));

  it('carries contextName only for the archive that has one', () => {
    const entries = quickEntries(sections);
    expect(entries.find((e) => e.label === 'Vol. 012')?.contextName).toBe('Berserk');
    expect(entries.find((e) => e.label === 'Loose')?.contextName).toBeUndefined();
    expect(entries.find((e) => e.label === 'Berserk')?.contextName).toBeUndefined();
  });

  it('renders the context line instead of the parent-folder detail; other entries keep their detail', () => {
    TestBed.configureTestingModule({ imports: [QuickResultsComponent] });
    const fixture = TestBed.createComponent(QuickResultsComponent);
    fixture.componentRef.setInput('sections', sections);
    fixture.detectChanges();
    const options = Array.from(fixture.nativeElement.querySelectorAll('.qr-option')) as HTMLElement[];
    const byLabel = (label: string) => options.find((o) => o.querySelector('.qr-label')?.textContent?.trim() === label)!;

    const volume = byLabel('Vol. 012');
    expect(volume.querySelector('[data-testid="context-line"]')?.textContent?.trim()).toBe('Berserk');
    expect(volume.querySelector('.qr-detail')).toBeNull();

    const loose = byLabel('Loose');
    expect(loose.querySelector('[data-testid="context-line"]')).toBeNull();
    expect(loose.querySelector('.qr-detail')?.textContent?.trim()).toBe('Manga');

    const folder = byLabel('Berserk');
    expect(folder.querySelector('[data-testid="context-line"]')).toBeNull();
    expect(folder.querySelector('.qr-detail')?.textContent?.trim()).toBe('Manga');
  });
});
