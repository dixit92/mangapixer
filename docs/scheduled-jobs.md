# Scheduled jobs

*New in 1.32.0.*

MangaPixer does some work on its own: it scans your libraries, backs up its database, refreshes series information and tidies up after itself. **MangaPixer Administration** > **Scheduled jobs** lists every one of these jobs with when it last ran, what it did and when it runs next. For the jobs that run once a day you choose the hour.

*Since 1.35.0* the section starts with **Library scans**, one row per library (its schedule, time, last and next scan), followed by the other jobs in three groups: **Web information** (automatic matching, series information refresh, volume covers, update check), **Library upkeep** (thumbnails, content signatures, cover choices) and **Maintenance** (backups, trash, cache, sign-in sessions). Each job shows what it does, its settings or when it runs, and its last and next run.

## Server time

All hours are **server time**: the clock of the computer or container MangaPixer runs on. The section says which zone that is, for example *Times are server time: America/New_York (UTC-04:00), now 14:05.* Times are shown in that zone even when you open the page from somewhere else, so they always match the hour you picked.

In Docker the zone comes from the container's `TZ` variable (Unraid sets it for you). Without `TZ` the container runs on UTC.

When the clocks change, a job set to an hour that is skipped that night runs an hour later; a job set to an hour that happens twice runs once.

## Jobs whose time you choose

| Job | Default | What you can choose |
|---|---|---|
| **Library scan** (one row per library in **Library scans**) | Daily, any time | The preset (Off, Hourly, Every 6 hours, Daily, Weekly) and, for Daily and Weekly, **At** (an hour) and for Weekly **On** (a weekday). The **Libraries** card shows the schedule under each library, with a link to this row. |
| **Series information refresh** | 03:00 | **Run at**, how often series are checked and (*1.45.0*) how often MangaPixer checks for new chapters (see below). |
| **Database backup** | Every 24 hours, any time | **At** an hour, when the backups run every day or every few days. The interval itself is on the **Database backup** card. |
| **Empty trash and clean bundles** | 04:00, only while automatic cleaning is on | **Turn automatic cleaning on** (off by default; it asks first and shows what the first run removes) and **Run at**. This is the only place to switch it: the [Trash](trash.md#automatic-cleaning) card shows the status and links here. |
| **Cache clean-up** | 05:00 | **Run at**. It removes the oldest cached pages when the cache is over its size limit. |

**Any time** means what it did before 1.32.0: the job runs one interval after its last run (a daily library scan one day after the last scan, manual or automatic). Libraries keep "any time" after an upgrade until you pick an hour.

A job that was due while the server was off runs **once** shortly after it starts again; it does not run several times to catch up, and a restart never runs a daily job a second time on the same day.

## Jobs that run on their own rhythm

These are listed so you can see that they run, but they have no time to choose: they either react as soon as something changes, or they are small local tasks.

| Job | Rhythm |
|---|---|
| **Automatic matching** | Continuous: new folders are matched within about a minute (when [Automatic matching](series-information.md#automatic-matching) is on). |
| **Volume covers and volume lists** | Continuous, together with automatic matching; each series on its refresh schedule. |
| **Cover choices** | Every 10 minutes. Nothing is sent anywhere. |
| **Content signatures** | Every 6 hours while older archives have no fingerprint yet. |
| **Volume contents** *(1.44.0)* | Hourly and after library scans, while **Find the chapters inside volume files** is on (Administration > **Volume contents**): reads volume files and the chapter files next to them to tell which chapters a volume file holds. Only reads files; nothing leaves the server. See [Volumes view](volumes.md#chapters-found-inside-a-volume-file). |
| **Sign-in session clean-up** | Hourly. |
| **Thumbnails** | Once after start-up. |
| **Update check** | When an admin opens Administration, at most once a day, while the [Update Checker](configuration.md#settings-stored-in-the-app) is on. |

## How often series information is refreshed

With [Automatic matching](series-information.md#background-refresh) on, MangaPixer fetches the series you have linked again from MangaUpdates, by record number, once a day at the **Run at** hour. These settings in the **Series information refresh** card decide which series are due:

- **Check ongoing series**: **Follow their pace** (the default), **Every week**, **Every 2 weeks** or **Every month**. A fixed choice checks every ongoing series that often.
- **Pace from** (*1.35.0*, only with **Follow their pace**): what the pace follows - **Whichever is faster** (the default), **New chapters (scanlations)** or **New volumes (original release)**.
- **Check finished series** (complete or cancelled): **Every 3 months** (the default), **Every month** or **Every 6 months**. While following the pace, a series on hiatus, or with nothing new for six months, is checked like a finished one too.

**Following the pace**, each ongoing series is checked by how often something new appears:

- **New chapters**: the latest chapter MangaUpdates lists, which follows scanlation releases, as MangaPixer saw it rise at earlier refreshes. *Since 1.45.0* a series is checked at about **half** the time between two of its new chapters - a chapter a month is checked every 15 days, a weekly one every 3 days (never more often than every 3 days, never less often than every month). Before 1.45.0 a new chapter every two weeks or less was checked every week, every two to four weeks every 2 weeks, less often every month - at or after the next chapter, so a monthly series was found half a month late on average. Until MangaPixer has seen two rises at least four weeks apart (*1.35.1*), it uses the average since the series began - the latest chapter over the time since its start year - which can only make a series look slower than it is, never faster; the series page then says "on average".
- **New volumes**: the original release's volumes, as they rose at earlier refreshes, else the average since the start year (or, without volumes, its chapters). A new volume every two months or less is checked every week, every two to four months every 2 weeks, less often every month.
- **Whichever is faster** takes the more frequent of the two. A series whose pace is not known yet is checked every month.

**A series someone is reading** (*1.45.0*) is checked **every 2 days**, whatever the choices above: one with an archive marked read in the
last 14 days, or with a tracker entry (MyAnimeList, MangaUpdates lists) whose status is **Reading**. A finished series whose translation is
complete as well is the exception: nothing new can come, so it keeps the finished schedule.

Before 1.35.0 the choice for ongoing series was also a limit on the pace (with **Every week** chosen, every ongoing series was checked weekly whatever its pace) and only volumes counted. **Upgrading:** if the pace was on, it stays on, and the choice for ongoing series is no longer read - choose **Every week** under **Check ongoing series** if you want every ongoing series checked weekly.

The pace comes only from what MangaPixer already knows; nothing extra is sent to find it. An admin sees each series' schedule and the pace that set it on its series page, for example *Checked every 2 weeks (a new chapter about every 4 weeks on average); next check in 9 days.*

**More than once a day** (*1.45.0*): the **Run at** hour stays the daily run, and between two daily runs MangaPixer refreshes the series that
have become due every 4 hours, so a series checked every 2 or 3 days is not held back to the next morning.

### Checking for new chapters

*Since 1.45.0*, with **Automatic matching** on, MangaPixer also looks out for new chapters between those checks:

- **MangaDex** (**Check for new chapters**: every 6 hours by default, or every 1 to 5 hours): one request for every 100 of your linked
  series that have a MangaDex record, sending only their MangaDex ids (and MangaDex's fixed content-rating list), reads the latest chapter
  MangaDex has had uploaded for each, in any language. When it changed, that series' MangaDex chapter and volume lists are read again right
  away (they show what was released in your preferred language).
- **MangaUpdates**: four times a day MangaPixer reads MangaUpdates' list of the day's releases (a few requests, sending only the page
  number). A linked series with a release above the chapter MangaPixer knows is refreshed right away. Nothing from the list is kept but
  which of your series had a release.
- When a refresh sees MangaUpdates' latest chapter rise, the series' MangaDex lists are read again right away too.

Every one of these requests counts in the daily request budget and is paced like the others; with Automatic matching off, or a library's
**Fetch from the web** off, nothing is sent for it. The card shows when MangaDex and MangaUpdates were last checked, how many series had a
new chapter and how many requests it took. A typical library of about 1,000 series with MangaDex records needs some 45 MangaDex requests
and at most 12 MangaUpdates requests a day for this.

An admin's **Refresh** of a series (*1.45.0*) counts like a scheduled one: the change it sees is noted for the series' pace, and it moves
only the next check of what it read again.

There is no separate limit on refreshes: everything that is due is refreshed, the series checked longest ago first, until the [daily request budget](series-information.md#admin-settings) is spent - the one limit for every request; what is left follows the next day, and the card says how many series are due now, how many were checked today and how many series follow each schedule.

The volume lists, volume covers and AniList totals of a series follow the same schedule, so choosing **Every week** also brings a new official volume cover sooner.

## The daily request budget

The [daily request budget](series-information.md#admin-settings) starts again at **midnight server time** (before 1.32.0: 00:00 UTC). On the day you upgrade, requests already counted that UTC day stay counted, so the change never spends extra.
