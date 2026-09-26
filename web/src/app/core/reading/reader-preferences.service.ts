import { Injectable, signal } from '@angular/core';
import { DownscaleFilter, DOWNSCALE_FILTERS } from './downscale-filters';

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
 * How an UPSCALED page is resampled for display (1.19.0; `sharp` 1.25.0). Only
 * relevant when the page is being painted LARGER than its natural size (a
 * small/old scan on a big screen); a downscale is already handled server-side.
 *  - `smooth` - the default and exactly today's behaviour: whatever the browser's
 *    built-in image smoothing does. Zero cost, works everywhere.
 *  - `sharp` - AMD FSR 1 (edge-adaptive upscale + contrast-adaptive sharpening) on
 *    WebGL2: two cheap GPU passes, no secure context needed, so it also works over
 *    plain `http://` on a LAN.
 *  - `enhance` - the Anime4K line-art upscaler, rendered into a canvas laid over
 *    the page: on WebGPU where the browser offers it (HTTPS or localhost only),
 *    otherwise on WebGL2 (Efficient chain only). Line art and screentones survive
 *    magnification far better than with bilinear smoothing.
 * Where the saved choice cannot run on this device the reader shows the page as
 * `smooth`, says so once per session, and the settings UI names the reason; the
 * stored value is kept, so the same choice works again on a capable connection.
 */
export type Upscaler = 'smooth' | 'sharp' | 'enhance';

/**
 * Which Anime4K network Enhance runs (1.24.0, owner decision 2026-09-25):
 *  - `balanced` - the default: the light M chain (`CNNM` + `CNNx2M`). Roughly
 *    40% less GPU memory and several times less GPU work than `max`, so it suits
 *    phones and tablets and saves battery.
 *  - `max` - "Max quality": the heavy VL chain (`CNNVL` + `CNNx2VL`), which is
 *    what paged Enhance used from 1.19.0 to 1.23.x.
 * Only the PAGED / double-page views honour `max`; the vertical (webtoon) view
 * always runs `balanced`, because it keeps many bands alive while scrolling.
 * `max` is WebGPU-only (1.25.0): Enhance on WebGL2 runs `balanced` and the menu
 * says so; the stored value is kept for a WebGPU connection.
 */
export type EnhanceQuality = 'balanced' | 'max';

// DownscaleFilter re-exported from downscale-filters.ts (single source of truth)
export type { DownscaleFilter };

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
  static readonly EnhanceQualityKey = 'mangapixer-reader-enhance-quality';

  /** Display-sized page requests are the default: less bandwidth, sharper pages. */
  static readonly DefaultPageQuality: PageQuality = 'auto';
  /**
   * Upscaling defaults to Crisp (FSR 1; owner, 2026-09-26: "good enough" and far lighter than
   * Enhance). Only while nothing is stored: a device that cannot run Crisp shows Smooth, and
   * because the user never chose, it is not announced (see `upscalerChosen`).
   */
  static readonly DefaultUpscaler: Upscaler = 'sharp';
  /** Mitchell is the new server default: a middle ground between Sharp and Soft. */
  static readonly DefaultDownscaleFilter: DownscaleFilter = 'balanced';
  /** 1.24.0: the light M chain by default; VL is the opt-in "Max quality". */
  static readonly DefaultEnhanceQuality: EnhanceQuality = 'balanced';

  /** How many pixels to request per page; see `PageQuality`. */
  readonly pageQuality = signal<PageQuality>(this.loadPageQuality());

  /** How an upscaled page is resampled for display; see `Upscaler`. */
  readonly upscaler = signal<Upscaler>(this.loadUpscaler());

  /**
   * Whether this device ever SAVED a Upscaling choice. A saved choice that cannot run is
   * announced once per session; the unsaved default quietly shows what can run.
   */
  readonly upscalerChosen = signal<boolean>(this.hasStoredUpscaler());

  /** Which resampling filter a sized-down page request asks for; see `DownscaleFilter`. */
  readonly downscaleFilter = signal<DownscaleFilter>(this.loadDownscaleFilter());

  /** Which Anime4K network Enhance runs; see `EnhanceQuality`. */
  readonly enhanceQuality = signal<EnhanceQuality>(this.loadEnhanceQuality());

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
    this.upscalerChosen.set(true);
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

  setEnhanceQuality(quality: EnhanceQuality): void {
    this.enhanceQuality.set(quality);
    try {
      localStorage.setItem(ReaderPreferencesService.EnhanceQualityKey, quality);
    } catch {
      /* storage unavailable (private mode) — keep the in-memory value */
    }
  }

  private loadEnhanceQuality(): EnhanceQuality {
    try {
      const raw = localStorage.getItem(ReaderPreferencesService.EnhanceQualityKey);
      if (raw === 'balanced' || raw === 'max') return raw;
    } catch {
      /* storage unavailable — fall through to the default */
    }
    return ReaderPreferencesService.DefaultEnhanceQuality;
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

  private hasStoredUpscaler(): boolean {
    try {
      const raw = localStorage.getItem(ReaderPreferencesService.UpscalerKey);
      return raw === 'smooth' || raw === 'sharp' || raw === 'enhance';
    } catch {
      return false;
    }
  }

  private loadUpscaler(): Upscaler {
    try {
      const raw = localStorage.getItem(ReaderPreferencesService.UpscalerKey);
      if (raw === 'smooth' || raw === 'sharp' || raw === 'enhance') return raw;
    } catch {
      /* storage unavailable — fall through to the default */
    }
    return ReaderPreferencesService.DefaultUpscaler;
  }

  private loadDownscaleFilter(): DownscaleFilter {
    try {
      const raw = localStorage.getItem(ReaderPreferencesService.DownscaleFilterKey);
      if ((DOWNSCALE_FILTERS as readonly string[]).includes(raw ?? '')) return raw as DownscaleFilter;
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
