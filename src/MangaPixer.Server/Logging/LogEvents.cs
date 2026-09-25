namespace com.lifepixer.mangapixer.Server.Logging;

/// <summary>
/// Structured event IDs for MangaPixer log events (audit gap 8.3.7).
///
/// IDs are grouped into per-category numeric ranges so events can be
/// filtered, grepped, and grouped by kind; <see cref="CategoryRanges"/> is the
/// single source of truth for the category list surfaced by the diagnostics
/// export (gap 8.3.10). Values are plain ints — <see cref="Microsoft.Extensions.Logging.EventId"/>
/// has an implicit conversion from int, so call sites read
/// <c>LogInformation(LogEvents.Auth.LoginSuccess, ...)</c>.
///
/// Ranges:
/// - 1xxx Database — startup configuration, schema/migration, recovery
/// - 2xxx Authentication — logins, lockouts, first-run setup
/// - 3xxx Scanning — scan phases, reconciliation, tombstoning
/// - 4xxx Worker — pool, supervisor, scheduler, scratch, persistence, delivery
/// - 5xxx Cache — hit/miss, publish, eviction, budget
/// - 6xxx Backup — manual/scheduled backups, restore, maintenance
/// - 7xxx Administration — admin actions (password reset, log-level change)
/// - 8xxx Http — request pipeline errors
/// - 9xxx Metadata — series metadata: ComicInfo backfill, links (1.24.0)
///
/// Event payloads follow the privacy invariant: IDs, counts, timings, and
/// sanitized error codes only — never paths, titles, entry names, or bytes.
/// </summary>
public static class LogEvents
{
    /// <summary>Category ranges, ordered. Used to derive the diagnostics export.</summary>
    public static readonly (string Name, int Min, int Max)[] CategoryRanges =
    [
        ("Database", Database.Min, Database.Max),
        ("Authentication", Auth.Min, Auth.Max),
        ("Scanning", Scanning.Min, Scanning.Max),
        ("Worker", Worker.Min, Worker.Max),
        ("Cache", Cache.Min, Cache.Max),
        ("Backup", Backup.Min, Backup.Max),
        ("Administration", Administration.Min, Administration.Max),
        ("Http", Http.Min, Http.Max),
        ("Metadata", Metadata.Min, Metadata.Max),
    ];

    /// <summary>Category names derived from the real event-ID ranges.</summary>
    public static string[] CategoryNames =>
        CategoryRanges.Select(r => r.Name).ToArray();

    /// <summary>Startup configuration, database schema/migration, and recovery events.</summary>
    public static class Database
    {
        public const int Min = 1000;
        public const int Max = 1999;

        public const int StartupStorageRoots = 1001;
        public const int StartupStorageBudgets = 1002;
        public const int StartupWorkerExecutable = 1003;
        public const int DataProtectionKeys = 1004;
        public const int FirstRunSetupPending = 1005;
        public const int DatabaseInitDegraded = 1006;
        public const int FatalShutdown = 1007;

        public const int MigrationBaselineAdopted = 1008;
        public const int MigrationWithBackup = 1009;
        public const int MigrationFreshDatabase = 1010;

        public const int InterruptedJobsFound = 1015;
        public const int InterruptedJobsRecovered = 1016;
        public const int NoInterruptedJobs = 1017;
        public const int StaleAnalysisCleared = 1018;
        public const int ScratchRecoverySkipped = 1019;
        public const int ScratchRecoveryScanning = 1020;
        public const int ScratchWorkspacesRecovered = 1021;
        public const int ScratchRecoveryNone = 1022;
        public const int DiskFullDerivedWorkStopped = 1023;
        public const int SchemaVersionMissing = 1025;
        public const int SchemaVersionChecked = 1026;
        public const int SchemaVersionNewer = 1027;
        public const int SchemaVersionOlderRecreate = 1028;
        public const int SchemaValidationFailedRecoverySkipped = 1030;
        public const int StartupRecoveryCompleted = 1031;
        public const int StartupRecoveryFailed = 1032;
    }

    /// <summary>Login, lockout, and first-run setup events.</summary>
    public static class Auth
    {
        public const int Min = 2000;
        public const int Max = 2999;

        public const int FirstAdminSignedIn = 2001;
        public const int LoginRejected429 = 2002;
        public const int LoginUnknownUser = 2003;
        public const int LoginDisabledUser = 2004;
        public const int AccountLockedOut = 2005;
        public const int LoginSucceeded = 2006;
        public const int PasswordChanged = 2007;

        public const int SetupAlreadyInitialized = 2010;
        public const int SetupUsernameMissing = 2011;
        public const int SetupPasswordTooShort = 2012;
        public const int SetupCreationRejected = 2013;
        public const int FirstAdminCreated = 2014;

        public const int RateLimitedByIp = 2020;
        public const int RateLimitedByUsername = 2021;
    }

    /// <summary>Scan phases, reconciliation, tombstoning, and admin-triggered scans.</summary>
    public static class Scanning
    {
        public const int Min = 3000;
        public const int Max = 3999;

        public const int ScanStarted = 3001;
        public const int ScanRootUnavailable = 3002;
        public const int ScanPhaseObservation = 3003;
        public const int ScanObservedCount = 3004;
        public const int ScanPhaseReconciliation = 3005;
        public const int ScanReconciliationComplete = 3006;
        public const int ScanPhaseTombstoning = 3007;
        public const int ScanCompleted = 3008;
        public const int ScanNoMissingNodes = 3009;
        public const int ScanMissingNodes = 3010;
        public const int SuspiciousLossPartial = 3011;
        public const int SuspiciousLossAll = 3012;
        public const int ScanTombstoned = 3013;

        public const int AdminScanCompleted = 3015;
        public const int AdminScanCancelled = 3016;
        public const int AdminScanFailed = 3017;
        public const int AnalysisEnqueueBatch = 3018;
        public const int AnalysisEnqueueItemFailed = 3019;
        public const int AnalysisEnqueueFailed = 3020;

        // 1.5.0 scan speed: move detection + phase timings
        public const int ScanMoveDetected = 3021;
        public const int ScanMoveAmbiguous = 3022;
        public const int ScanMovesApplied = 3023;
        public const int ScanPhaseTimings = 3024;

        // 1.23.0 scheduled scans
        public const int ScheduledScanStarted = 3025;
        public const int ScheduledScanSkippedRootUnavailable = 3026;
        public const int ScheduledScanRootAvailableAgain = 3027;
        public const int ScheduledScanUnrecognisedSchedule = 3028;
        public const int ScheduledScanEvaluationFailed = 3029;
        public const int ScheduledScanDisabled = 3030;
        public const int ScheduledScanFinished = 3031;
    }

    /// <summary>Worker pool, supervisor, scheduler, scratch, persistence, and page delivery.</summary>
    public static class Worker
    {
        public const int Min = 4000;
        public const int Max = 4999;

        // MediaWorkerPool
        public const int PoolStarting = 4001;
        public const int PoolStarted = 4002;
        public const int PoolScaleUp = 4003;
        public const int PoolScaleUpFailed = 4004;
        public const int PoolSaturatedJobQueued = 4005;
        public const int PoolIdleSlotReleased = 4006;
        public const int JobDispatched = 4007;
        public const int ExtractDispatchFailed = 4008;
        public const int ExtractSlotAcquire = 4009;
        public const int ExtractWorkerStartFailed = 4010;
        public const int ExtractSlotAcquireTimeout = 4011;
        public const int PoolStopping = 4012;
        public const int PoolStopWorkerError = 4013;
        public const int PoolStopped = 4014;
        public const int WorkerProcessStartAttempt = 4015;
        public const int WorkerExited = 4016;
        public const int WorkerRemovedFromPool = 4017;
        public const int WorkerReadyInPool = 4018;
        public const int WorkerStartFailed = 4019;
        public const int WorkerExecutableFallback = 4020;
        public const int JobSourceStampRejected = 4022;
        // 4021/4023/4025 retired: per-job dispatch/completion is logged once by
        // the pool/scheduler respectively; the duplicates were removed.

        public const int JobTimedOut = 4024;
        public const int JobProcessingFailed = 4026;
        public const int PersistAfterJobFailed = 4027;
        public const int WorkerRetiredIdle = 4028;
        public const int WorkerRetireFailed = 4029;

        public const int HostedPoolStarted = 4031;
        public const int PoolStartFailed = 4032;
        public const int PoolStopError = 4033;
        public const int DispatchLoopError = 4034;
        public const int AnalysisThroughputSummary = 4035;

        public const int SupervisorProcessStarting = 4041;
        public const int SupervisorProcessStarted = 4042;
        public const int SupervisorHandshakeReady = 4043;
        public const int SupervisorStartupPaused = 4044;
        public const int SupervisorStartupBackoff = 4045;
        public const int SupervisorMalformedMessage = 4046;
        public const int SupervisorMessageReceived = 4047;
        public const int SupervisorGracefulStop = 4048;
        public const int SupervisorGraceExpiredForceKill = 4049;
        public const int SupervisorProtocolVersionMismatch = 4050;
        public const int SupervisorStderrLine = 4051;
        public const int SupervisorProcessExited = 4052;

        public const int SchedulerJobDedupedInFlight = 4061;
        public const int SchedulerPriorityUpgraded = 4062;
        public const int SchedulerEnqueueRace = 4063;
        public const int SchedulerJobEnqueued = 4064;
        // 4065/4066 retired: dequeue and in-flight marking are covered by the
        // pool's single dispatch log; separate scheduler-side lines were noise.
        public const int SchedulerJobCompleted = 4067;
        public const int SchedulerJobFailed = 4068;
        public const int SchedulerPendingCancelled = 4069;

        public const int ScratchWorkspaceAllocated = 4071;
        public const int ScratchCleanupSkippedUnowned = 4072;
        public const int ScratchCleanupFailed = 4073;
        public const int ScratchRecoveryCleanupFailed = 4074;
        public const int ScratchRecoveryPassComplete = 4075;

        public const int PersistSkippedMissingItem = 4081;
        public const int PersistFailureRecorded = 4082;
        public const int PersistInvalidResult = 4083;
        public const int PersistCompleted = 4084;

        public const int PageCachePublishFailed = 4085;
        public const int PageExtractionFailed = 4086;

        public const int PageDeniedItemMissing = 4091;
        public const int PageDeniedUserInactive = 4092;
        public const int PageDeniedNoGrant = 4093;
        public const int PageDeniedArchiveMissing = 4094;
        public const int PageNotReady = 4095;
        public const int PageServedFromCache = 4096;
        public const int PageCacheMiss = 4097;
        public const int CoverServedFromCache = 4098;
        public const int CoverCacheMiss = 4099;

        // Durable thumbnail store (1.2.0) — generation, persistence, serving
        public const int ThumbnailGenerated = 4101;
        public const int ThumbnailGenerationFailed = 4102;
        public const int ThumbnailGenerationSkipped = 4103;
        public const int ThumbnailServedFromStore = 4104;
        public const int ThumbnailStoreMiss = 4105;
        public const int ThumbnailBackfillEnqueued = 4106;
        public const int ThumbnailBackfillBatch = 4107;
        public const int ThumbnailStaleInvalidated = 4108;
    }

    /// <summary>Derived-cache lifecycle: hits, publishes, eviction, budget pressure.</summary>
    public static class Cache
    {
        public const int Min = 5000;
        public const int Max = 5999;

        public const int CacheHit = 5001;
        public const int CacheEntryFileMissing = 5002;
        public const int CachePublished = 5003;
        public const int CachePublishedFromStream = 5004;
        public const int CacheOverBudget = 5005;
        public const int CacheEvictEntryFailed = 5006;
        public const int CacheEvictedBatch = 5007;
        public const int CacheDiskFull = 5008;
        public const int CacheEvictionFreed = 5009;
    }

    /// <summary>Manual and scheduled backups, restore, and maintenance.</summary>
    public static class Backup
    {
        public const int Min = 6000;
        public const int Max = 6999;

        public const int BackupStarting = 6001;
        public const int BackupVerificationFailed = 6002;
        public const int BackupCompleted = 6003;
        public const int BackupFailed = 6004;
        public const int RestoreAbortedVerification = 6005;
        public const int RestoreCompleted = 6006;
        public const int RestoreFailed = 6007;
        public const int SessionsInvalidated = 6008;

        public const int RotatingRunFailed = 6010;
        public const int RotatingRunCompleted = 6011;
        public const int BackupLocationMarkerInitialized = 6012;
        public const int RotatingLocationUnavailable = 6013;
        public const int RotatingLocationRecovered = 6014;
        public const int RotatingDisabled = 6015;
        public const int ScheduledRotatingFailed = 6016;
        public const int ScheduledRotatingError = 6017;
        public const int BackupSettingsChanged = 6018;
        public const int BackupConfigInvalid = 6019;

        public const int MaintenanceFailed = 6020;

        // 1.7.0 DB backup import/restore — staged upload + apply-on-restart
        public const int RestoreUploadRejected = 6030;
        public const int RestorePreSnapshotFailed = 6031;
        public const int RestoreStaged = 6032;
        public const int RestoreApplyPending = 6033;
        public const int RestoreApplied = 6034;
        public const int RestoreApplyFailed = 6035;
        public const int RestoreRolledBack = 6036;
        public const int SafetySnapshotsPruned = 6037;

        // 1.23.0 move existing rotating snapshots on a location change
        public const int SnapshotMoveStarted = 6040;
        public const int SnapshotMoveCompleted = 6041;
        public const int SnapshotMoveFailed = 6042;
    }

    /// <summary>Admin actions on users and server configuration.</summary>
    public static class Administration
    {
        public const int Min = 7000;
        public const int Max = 7999;

        public const int AdminPasswordReset = 7010;
        public const int LogLevelChanged = 7011;
        public const int YacReaderImportPreviewed = 7012;
        public const int YacReaderImportApplied = 7013;
        public const int YacReaderImportFailed = 7014;
        public const int LibraryDeleted = 7015;
        public const int LogCategoryLevelChanged = 7016;
        public const int LogCategoryLevelCleared = 7017;
        public const int ActivationTokenCreated = 7020;
        public const int ActivationSucceeded = 7021;
        public const int ActivationFailedInvalidToken = 7022;
        public const int ActivationFailedExpiredToken = 7023;
        public const int ActivationFailedConsumedToken = 7024;
        public const int UserDeleted = 7025;
        public const int ActivationReissued = 7026;
    }

    /// <summary>App-level request pipeline errors (unhandled 500s).</summary>
    public static class Http
    {
        public const int Min = 8000;
        public const int Max = 8999;

        public const int UnhandledRequestError = 8001;
    }

    /// <summary>
    /// Series metadata (1.24.0). Payloads carry node/library/record ids, counts,
    /// status codes and timings ONLY - never titles, ComicInfo values, search
    /// text, URLs or paths.
    /// </summary>
    public static class Metadata
    {
        public const int Min = 9000;
        public const int Max = 9999;

        // ComicInfo backfill (B1)
        public const int ComicInfoBackfillStarted = 9001;
        public const int ComicInfoBackfillCompleted = 9002;
        public const int ComicInfoBackfillFailed = 9003;
        public const int ComicInfoReadFailed = 9004;
        public const int ComicInfoPersistFailed = 9005;

        // Admin link / settings changes (B1)
        public const int SeriesLinkChanged = 9020;
        public const int PrecedenceChanged = 9021;
        public const int SettingsChanged = 9022;
        public const int Purged = 9023;

        // 9100-9199 reserved for the network gateway / providers (lane B2).
    }
}
