import { Injectable, signal } from '@angular/core';

/**
 * Page-turn transition for the PAGED / DOUBLE-SPREAD reader (1.9.0). This is a
 * presentation nicety only — it never changes which page is shown, only how the
 * new page appears:
 *  - `slide`   — the incoming page eases in from the direction of travel
 *                (reading-direction aware), replacing the 1.8.x "snap back then
 *                 swap" jank on a page turn. The tasteful default.
 *  - `reveal`  — a quick cross-fade of the incoming page (no horizontal motion).
 *  - `none`    — instant swap (the pre-1.9.0 behaviour).
 *
 * The webtoon (vertical scroll) view is never affected: its page changes are
 * driven by native scrolling, not a discrete swap, so a transition would fight
 * the scroll. `prefers-reduced-motion` is honoured in CSS (the animation is
 * disabled there regardless of this setting), so this remains a pure preference.
 */
export type PageAnimation = 'slide' | 'reveal' | 'none';

/**
 * How many pixels to ask the server for (1.19.0, "Image Scaling"):
 *  - `auto` — the default. The reader sizes each request to the box the page is
 *    about to be painted into (`targetMaxDim` in `page-variant.ts`) and appends
 *    `?maxDim=<bucket>`, so a phone gets a ~1080px-edge page instead of a full
 *    4K scan. Fewer bytes AND a sharper picture, because the downscale is the
 *    server's Lanczos filter rather than the browser's cheap one.
 *  - `full` — never append `maxDim`: always the full-size transcode, exactly the
 *    pre-1.19.0 behaviour. For a reader who would rather spend the bandwidth
 *    (pinch-zooming into artwork, an archival-quality display, a slow server).
 *
 * The `original` fit mode always requests full resolution regardless of this
 * setting, since "Original size" is itself a request for native pixels.
 */
export type PageQuality = 'auto' | 'full';

/**
 * How an UPSCALED page is resampled for display (1.19.0). Only relevant when the
 * page is being painted LARGER than its natural size (a small/old scan on a big
 * screen); a downscale is already handled server-side.
 *  - `smooth` — the default and exactly today's behaviour: whatever the browser's
 *    built-in image smoothing does. Zero cost, works everywhere.
 *  - `enhance` — a GPU (WebGPU) line-art upscaler, Anime4K, rendered into a
 *    canvas laid over the page. Line art and screentones survive magnification
 *    far better than with bilinear smoothing. Needs WebGPU; where it is missing
 *    the reader silently renders as `smooth` and the settings UI says so.
 */
export type Upscaler = 'smooth' | 'enhance';

/**
 * Which resampling filter the server uses when it downscales a page (1.20.0,
 * "Downscale filter"). Only takes effect when `pageQuality` is `auto` AND the
 * request lands on a sized bucket (`maxDim` — see `page-variant.ts`); Full page
 * quality and `original` fit never downscale, so the filter has nothing to act
 * on there.
 *  - `sharp`    — Lanczos, the 1.19.x behaviour: crisp lines, can moire on
 *                 screentones.
 *  - `balanced` — Mitchell. The default: a middle ground between the other two.
 *  - `soft`     — area average: kills screentone moire, slightly softer lines.
 */
export type DownscaleFilter = 'sharp' | 'balanced' | 'soft';

/**
 * Per-DEVICE reader preferences kept in `localStorage` (never a backend/EF
 * preference — deliberately device-local, like the existing webtoon-width and
 * page-mode prefs in the reader). Covers:
 *  - the page-navigation animation (above), and
 *  - the one-shot onboarding "help seen" flag that auto-shows the reader help
 *    overlay the first time this device opens the reader.
 *
 * Every access is wrapped in try/catch so the service degrades gracefully when
 * storage is unavailable (private-mode Safari, storage disabled), exactly as the
 * reader component already does for its other localStorage-backed prefs.
 */
@Injectable({ providedIn: 'root' })
export class ReaderPreferencesService {
  static readonly PageAnimationKey = 'mangapixer-reader-page-animation';
  static readonly HelpSeenKey = 'mangapixer-reader-help-seen';

  /** Default page-turn transition (owner request 1.9.0): a tasteful slide. */
  static readonly DefaultPageAnimation: PageAnimation = 'slide';

  /** Current page-turn transition; reactive so the reader re-evaluates live. */
  readonly pageAnimation = signal<PageAnimation>(this.loadPageAnimation());

  setPageAnimation(mode: PageAnimation): void {
    this.pageAnimation.set(mode);
    try {
      localStorage.setItem(ReaderPreferencesService.PageAnimationKey, mode);
    } catch {
      /* storage unavailable (private mode) — keep the in-memory value */
    }
  }

  private loadPageAnimation(): PageAnimation {
    try {
      const raw = localStorage.getItem(ReaderPreferencesService.PageAnimationKey);
      if (raw === 'slide' || raw === 'reveal' || raw === 'none') return raw;
    } catch {
      /* storage unavailable — fall through to the default */
    }
    return ReaderPreferencesService.DefaultPageAnimation;
  }

  // --- 1.19.0 image-scaling preferences ---------------------------------------

  static readonly PageQualityKey = 'mangapixer-reader-page-quality';
  static readonly UpscalerKey = 'mangapixer-reader-upscaler';
  static readonly DownscaleFilterKey = 'mangapixer-reader-downscale-filter';

  /** Display-sized page requests are the default: less bandwidth, sharper pages. */
  static readonly DefaultPageQuality: PageQuality = 'auto';
  /** Rendering defaults to the browser's own resampling (pre-1.19.0 behaviour). */
  static readonly DefaultUpscaler: Upscaler = 'smooth';
  /** Mitchell is the new server default: a middle ground between Sharp and Soft. */
  static readonly DefaultDownscaleFilter: DownscaleFilter = 'balanced';

  /** How many pixels to request per page; see `PageQuality`. */
  readonly pageQuality = signal<PageQuality>(this.loadPageQuality());

  /** How an upscaled page is resampled for display; see `Upscaler`. */
  readonly upscaler = signal<Upscaler>(this.loadUpscaler());

  /** Which resampling filter a sized-down page request asks for; see `DownscaleFilter`. */
  readonly downscaleFilter = signal<DownscaleFilter>(this.loadDownscaleFilter());

  setPageQuality(quality: PageQuality): void {
    this.pageQuality.set(quality);
    try {
      localStorage.setItem(ReaderPreferencesService.PageQualityKey, quality);
    } catch {
      /* storage unavailable (private mode) — keep the in-memory value */
    }
  }

  setUpscaler(upscaler: Upscaler): void {
    this.upscaler.set(upscaler);
    try {
      localStorage.setItem(ReaderPreferencesService.UpscalerKey, upscaler);
    } catch {
      /* storage unavailable (private mode) — keep the in-memory value */
    }
  }

  setDownscaleFilter(filter: DownscaleFilter): void {
    this.downscaleFilter.set(filter);
    try {
      localStorage.setItem(ReaderPreferencesService.DownscaleFilterKey, filter);
    } catch {
      /* storage unavailable (private mode) — keep the in-memory value */
    }
  }

  private loadPageQuality(): PageQuality {
    try {
      const raw = localStorage.getItem(ReaderPreferencesService.PageQualityKey);
      if (raw === 'auto' || raw === 'full') return raw;
    } catch {
      /* storage unavailable — fall through to the default */
    }
    return ReaderPreferencesService.DefaultPageQuality;
  }

  private loadUpscaler(): Upscaler {
    try {
      const raw = localStorage.getItem(ReaderPreferencesService.UpscalerKey);
      if (raw === 'smooth' || raw === 'enhance') return raw;
    } catch {
      /* storage unavailable — fall through to the default */
    }
    return ReaderPreferencesService.DefaultUpscaler;
  }

  private loadDownscaleFilter(): DownscaleFilter {
    try {
      const raw = localStorage.getItem(ReaderPreferencesService.DownscaleFilterKey);
      if (raw === 'sharp' || raw === 'balanced' || raw === 'soft') return raw;
    } catch {
      /* storage unavailable — fall through to the default */
    }
    return ReaderPreferencesService.DefaultDownscaleFilter;
  }

  /**
   * Has the reader help overlay already been auto-shown (onboarding) on this
   * device? A missing/unreadable flag reads as "not seen", so a fresh device (or
   * cleared storage) triggers the one-time auto-show.
   */
  hasSeenHelp(): boolean {
    try {
      return localStorage.getItem(ReaderPreferencesService.HelpSeenKey) === '1';
    } catch {
      /* storage unavailable — treat as unseen, but markHelpSeen() will also fail,
         so the overlay simply auto-shows again next time rather than never */
      return false;
    }
  }

  /** Record that onboarding help has been shown, so it never auto-shows again. */
  markHelpSeen(): void {
    try {
      localStorage.setItem(ReaderPreferencesService.HelpSeenKey, '1');
    } catch {
      /* storage unavailable — nothing to persist */
    }
  }
}
