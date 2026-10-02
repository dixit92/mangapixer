# Scheduled jobs

*New in 1.32.0.*

MangaPixer does some work on its own: it scans your libraries, backs up its database, refreshes series information and tidies up after itself. **MangaPixer Administration** > **Scheduled jobs** lists every one of these jobs with when it last ran, what it did and when it runs next. For the jobs that run once a day you choose the hour.

## Server time

All hours are **server time**: the clock of the computer or container MangaPixer runs on. The section says which zone that is, for example *Times are server time: America/New_York (UTC-04:00), now 14:05.* Times are shown in that zone even when you open the page from somewhere else, so they always match the hour you picked.

In Docker the zone comes from the container's `TZ` variable (Unraid sets it for you). Without `TZ` the container runs on UTC.

When the clocks change, a job set to an hour that is skipped that night runs an hour later; a job set to an hour that happens twice runs once.

## Jobs whose time you choose

| Job | Default | What you can choose |
|---|---|---|
| **Library scan** (one row per library) | Daily, any time | The preset (Off, Hourly, Every 6 hours, Daily, Weekly) and, for Daily and Weekly, **At** (an hour) and for Weekly **On** (a weekday). The same control is under each library on the **Libraries** card. |
| **Series information refresh** | 03:00 | **Run at**, and how often series are checked (see below). |
| **Database backup** | Every 24 hours, any time | **At** an hour, when the backups run every day or every few days. The interval itself is on the **Database backup** card. |
| **Empty trash and clean bundles** | 04:00, only while automatic cleaning is on | **Run automatic cleaning at** - the same setting as on the [Trash](trash.md#automatic-cleaning) card. |
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
| **Sign-in session clean-up** | Hourly. |
| **Thumbnails** | Once after start-up. |
| **Update check** | When an admin opens Administration, at most once a day, while the [Update Checker](configuration.md#settings-stored-in-the-app) is on. |

## How often series information is refreshed

With [Automatic matching](series-information.md#background-refresh) on, MangaPixer fetches the series you have linked again from MangaUpdates, by record number, once a day at the **Run at** hour. Three settings in the **Series information refresh** row decide which series are due:

- **Check ongoing series**: **Every month** (the default), **Every 2 weeks** or **Every week**.
- **Check finished series** (complete or cancelled): **Every 3 months** (the default), **Every month** or **Every 6 months**.
- **Follow each series' publishing pace** (on by default): a series that publishes quickly is checked more often, never more often than once a week and never less often than your choice for ongoing series. A series on hiatus, or with nothing new for six months, is checked like a finished one.

The pace comes from what MangaPixer already knows - the series' start year, its number of volumes or chapters, and how these changed at earlier refreshes. Nothing extra is sent to find it. Roughly: a series with a new volume every two months or less is checked every week, one with a volume every two to four months every 2 weeks, and the rest at your choice for ongoing series. An admin sees each series' schedule and the reason on its series page, for example *Checked every 2 weeks (a new volume about every 2 months); next check in 9 days.*

At most **200** series are refreshed a day. If more are due, the ones checked longest ago go first and the rest follow the next day; the row then says how many series are past their check date. The row also shows how many were checked today and how many series follow each schedule.

The volume lists, volume covers and AniList totals of a series follow the same schedule, so choosing **Every week** also brings a new official volume cover sooner.

## The daily request budget

The [daily request budget](series-information.md#admin-settings) and the refresh limit start again at **midnight server time** (before 1.32.0: 00:00 UTC). On the day you upgrade, requests already counted that UTC day stay counted, so the change never spends extra.
