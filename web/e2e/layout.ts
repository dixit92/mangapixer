import { expect, Page } from '@playwright/test';

/**
 * Generic layout checks for small screens (1.29.2): a page must fit the viewport. Presence checks alone let a phone
 * layout break unnoticed - the review rows' titles collapsed to one letter per line at 390 px (1.28.0 - 1.29.1) while
 * every phone test passed. Checked after the page settles:
 *
 * - `page-overflow`: the document scrolls sideways;
 * - `offscreen`: a visible element reaches past the left or right edge of the viewport (an element inside a container
 *   that scrolls sideways on purpose - `overflow-x: auto | scroll` - is fine, it can be scrolled into view);
 * - `squeezed`: text squeezed into a column narrower than about three characters that wraps onto several lines;
 * - `spills` (1.50.1): an icon drawn past the border of the button that holds it - a flex row squeezed a button below its
 *   content and Material centred the content, so the icon crossed the border (the phone library bar, 1.47.0 - 1.50.0).
 *
 * Only the outermost offending element of a tree is reported. Hidden elements (display / visibility / zero size /
 * `aria-hidden` trees, a closed side panel) are skipped.
 */
export interface LayoutProblem {
  kind: 'page-overflow' | 'offscreen' | 'squeezed' | 'spills';
  what: string;
  detail: string;
}

export async function layoutProblems(page: Page): Promise<LayoutProblem[]> {
  return page.evaluate(() => {
    const problems: { kind: 'page-overflow' | 'offscreen' | 'squeezed' | 'spills'; what: string; detail: string }[] = [];
    const vw = document.documentElement.clientWidth;
    const root = document.scrollingElement ?? document.documentElement;
    if (root.scrollWidth > vw + 1) {
      problems.push({ kind: 'page-overflow', what: 'document', detail: `scrollWidth ${root.scrollWidth} > ${vw}` });
    }

    const describe = (el: Element): string => {
      const id = el.getAttribute('data-testid');
      const cls = typeof el.className === 'string' ? el.className.trim().split(/\s+/).slice(0, 2).join('.') : '';
      const text = (el.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 30);
      return `<${el.tagName.toLowerCase()}${id ? ` data-testid=${id}` : ''}${cls ? ` .${cls}` : ''}>${text ? ` "${text}"` : ''}`;
    };
    const hidden = (el: Element): boolean => {
      if (el.closest('[aria-hidden="true"], [hidden], [inert]')) return true;
      const s = getComputedStyle(el);
      return s.display === 'none' || s.visibility === 'hidden' || s.visibility === 'collapse' || Number(s.opacity) === 0;
    };
    const drawnHidden = (el: Element): boolean => {
      const s = getComputedStyle(el);
      return s.display === 'none' || s.visibility === 'hidden' || s.visibility === 'collapse' || Number(s.opacity) === 0;
    };
    const scrollsSideways = (el: Element): boolean => {
      for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
        const x = getComputedStyle(p).overflowX;
        if (x === 'auto' || x === 'scroll') return true;
        // Angular Material's tab header pages its tabs with arrow buttons inside a clipped strip.
        if (p.classList.contains('mat-mdc-tab-label-container')) return true;
      }
      return false;
    };
    const isIcon = (el: Element): boolean =>
      el.tagName === 'MAT-ICON' || el.classList.contains('material-icons') || /Material (Icons|Symbols)/.test(getComputedStyle(el).fontFamily);

    const reported: Element[] = [];
    const under = (el: Element) => reported.some((r) => r.contains(el));
    for (const el of Array.from(document.body.querySelectorAll('*'))) {
      if (el instanceof SVGElement && !(el instanceof SVGSVGElement)) continue;
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0 || under(el)) continue;

      // Material marks every mat-icon aria-hidden (decorative), so the icon rule asks only whether it is drawn; its button must
      // still be outside any hidden tree (a closed panel).
      if (el.tagName === 'MAT-ICON' && !drawnHidden(el)) {
        const button = el.parentElement?.closest('button, [role="button"]');
        if (button && !hidden(button)) {
          // Inside the border, not just the outer box: a squeezed outlined button draws its icon ON the border line first.
          const o = button.getBoundingClientRect();
          const cs = getComputedStyle(button);
          const b = {
            left: o.left + parseFloat(cs.borderLeftWidth), right: o.right - parseFloat(cs.borderRightWidth),
            top: o.top + parseFloat(cs.borderTopWidth), bottom: o.bottom - parseFloat(cs.borderBottomWidth),
          };
          if (r.left < b.left - 0.5 || r.right > b.right + 0.5 || r.top < b.top - 0.5 || r.bottom > b.bottom + 0.5) {
            problems.push({ kind: 'spills', what: describe(button), detail: `icon ${Math.round(r.left)}-${Math.round(r.right)}, button ${Math.round(b.left)}-${Math.round(b.right)}` });
            reported.push(el);
            continue;
          }
        }
      }

      if (hidden(el)) continue;

      if ((r.left < -1 || r.right > vw + 1) && !scrollsSideways(el)) {
        // A fixed / transformed panel parked off-screen while closed is not part of the layout.
        const parked = r.right <= 0 || r.left >= vw;
        if (!parked) {
          problems.push({ kind: 'offscreen', what: describe(el), detail: `left ${Math.round(r.left)}, right ${Math.round(r.right)}, viewport ${vw}` });
          reported.push(el);
          continue;
        }
      }

      const ownText = Array.from(el.childNodes).filter((n) => n.nodeType === Node.TEXT_NODE).map((n) => n.textContent ?? '').join('').trim();
      if (ownText.length >= 4 && !isIcon(el)) {
        const font = parseFloat(getComputedStyle(el).fontSize) || 16;
        if (r.width < 3 * font && r.height > 3.5 * font) {
          problems.push({ kind: 'squeezed', what: describe(el), detail: `${Math.round(r.width)} x ${Math.round(r.height)} px at font ${font}px` });
          reported.push(el);
        }
      }
    }
    return problems;
  });
}

/** Fails with a readable list when the current page does not fit the viewport. `where` names the page in the message. */
export async function expectFitsScreen(page: Page, where: string): Promise<void> {
  const problems = await layoutProblems(page);
  const lines = problems.slice(0, 12).map((p) => `  ${p.kind}: ${p.what} - ${p.detail}`);
  expect(problems, `${where} does not fit ${page.viewportSize()?.width} px:\n${lines.join('\n')}`).toEqual([]);
}

/**
 * 1.52.0: on a phone in portrait the selection bar shows Select, Mark read and More; Mark unread, Favorites and the admin / tracker
 * actions wait behind More. Opens it where it is shown (a no-op on a wider screen, where every action is in the bar).
 */
export async function openSelectionMore(page: Page): Promise<void> {
  const more = page.getByTestId('selection-more');
  if (await more.isVisible() && (await more.getAttribute('aria-expanded')) !== 'true') await more.click();
}
