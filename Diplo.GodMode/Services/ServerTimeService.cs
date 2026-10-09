using System.Diagnostics;
using System.Globalization;
using Diplo.GodMode.Models;
using Diplo.GodMode.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;

namespace Diplo.GodMode.Services
{
    /// <summary>
    /// Reports the server clock and time zone, and checks how Umbraco 17's switch to UTC system dates has landed
    /// </summary>
    public class ServerTimeService : IServerTimeService
    {
        /// <summary>
        /// Scheduled publishing runs every minute, so anything this late has been missed.
        /// </summary>
        private const int OverdueAfterMinutes = 5;

        /// <summary>
        /// Rows dated further ahead than this are treated as suspicious rather than clock skew.
        /// </summary>
        private static readonly TimeSpan FutureDateTolerance = TimeSpan.FromMinutes(10);

        private static readonly DateTime FallbackStartedUtc = DateTime.UtcNow;

        private static readonly string[] MigrationUtcIdentifiers = ["Coordinated Universal Time", "UTC"];

        private readonly TimeProvider timeProvider;
        private readonly IOptions<SystemDateMigrationSettings> migrationSettings;
        private readonly IRuntimeState runtimeState;
        private readonly IServerRoleAccessor serverRoleAccessor;
        private readonly IUmbracoDatabaseService databaseService;
        private readonly IGodModeLogService logService;
        private readonly ILogger<ServerTimeService> logger;

        public ServerTimeService(
            TimeProvider timeProvider,
            IOptions<SystemDateMigrationSettings> migrationSettings,
            IRuntimeState runtimeState,
            IServerRoleAccessor serverRoleAccessor,
            IUmbracoDatabaseService databaseService,
            IGodModeLogService logService,
            ILogger<ServerTimeService> logger)
        {
            this.timeProvider = timeProvider;
            this.migrationSettings = migrationSettings;
            this.runtimeState = runtimeState;
            this.serverRoleAccessor = serverRoleAccessor;
            this.databaseService = databaseService;
            this.logService = logService;
            this.logger = logger;
        }

        public ServerTimeInfo GetServerTime()
        {
            var utcNow = timeProvider.GetUtcNow();
            var zone = timeProvider.LocalTimeZone;
            var offset = zone.GetUtcOffset(utcNow);
            var (ianaId, windowsId) = GetZoneIds(zone);
            var nextTransition = GetNextTransition(zone, utcNow);
            var culture = CultureInfo.CurrentCulture;

            return new ServerTimeInfo
            {
                ServerUtcNow = utcNow.UtcDateTime,
                ServerLocalNow = TimeZoneInfo.ConvertTime(utcNow, zone),
                TimeZoneId = zone.Id,
                IanaId = ianaId,
                WindowsId = windowsId,
                DisplayName = zone.DisplayName,
                StandardName = zone.StandardName,
                DaylightName = zone.DaylightName,
                UtcOffset = FormatOffset(offset),
                UtcOffsetMinutes = (int)offset.TotalMinutes,
                BaseUtcOffsetMinutes = (int)zone.BaseUtcOffset.TotalMinutes,
                IsUtc = zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime,
                SupportsDaylightSavingTime = zone.SupportsDaylightSavingTime,
                IsDaylightSavingTime = zone.IsDaylightSavingTime(utcNow),
                NextTransition = nextTransition.HasValue ? TimeZoneInfo.ConvertTime(nextTransition.Value, zone) : null,
                NextTransitionUtcOffset = nextTransition.HasValue ? FormatOffset(zone.GetUtcOffset(nextTransition.Value)) : string.Empty,
                TzEnvironmentVariable = Environment.GetEnvironmentVariable("TZ") ?? string.Empty,
                TimeProviderType = timeProvider.GetType().FullName ?? timeProvider.GetType().Name,
                CultureName = culture.Name,
                ShortDatePattern = culture.DateTimeFormat.ShortDatePattern,
                LongTimePattern = culture.DateTimeFormat.LongTimePattern,
                ProcessStartedUtc = GetProcessStartedUtc(),
                SystemDateMigration = GetMigrationInfo(zone, utcNow)
            };
        }

        public SystemDateEvidence GetSystemDateEvidence()
        {
            var evidence = new SystemDateEvidence();

            try
            {
                evidence.MigrationLogEntries = logService
                    .GetLogs(1, 10, null, null, null, "MigrateSystemDatesToUtc", null)
                    .Items
                    .Select(x => new SystemDateLogEntry
                    {
                        Timestamp = x.Timestamp,
                        Level = x.Level,
                        Message = x.Message
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not search the logs for the UTC system date migration.");
            }

            try
            {
                evidence.FutureDatedRows = databaseService
                    .GetFutureDatedRows(timeProvider.GetUtcNow().UtcDateTime.Add(FutureDateTolerance))
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not check for future dated rows.");
            }

            return evidence;
        }

        public ContentScheduleOverview GetContentSchedules()
        {
            var utcNow = timeProvider.GetUtcNow().UtcDateTime;
            var overdueCutoff = utcNow.AddMinutes(-OverdueAfterMinutes);

            var items = databaseService.GetContentSchedules().ToList();
            foreach (var item in items)
            {
                item.IsOverdue = DateTime.SpecifyKind(item.Date, DateTimeKind.Utc) < overdueCutoff;
            }

            var jobs = databaseService.GetDistributedJobs().ToList();
            foreach (var job in jobs)
            {
                // Allow a few missed periods (and at least five minutes) before calling a job stale.
                var allowance = TimeSpan.FromSeconds(Math.Max(job.PeriodSeconds * 3, 300));
                job.IsStale = DateTime.SpecifyKind(job.LastRun, DateTimeKind.Utc) < utcNow - allowance;
            }

            return new ContentScheduleOverview
            {
                ServerUtcNow = utcNow,
                OverdueAfterMinutes = OverdueAfterMinutes,
                Items = items,
                Jobs = jobs,
                Servers = GetRegisteredServers(utcNow),
                CurrentServerRole = GetCurrentServerRole()
            };
        }

        private IEnumerable<RegisteredServerInfo> GetRegisteredServers(DateTime utcNow)
        {
            try
            {
                return databaseService.GetRegistredServers()
                    .Select(x => new RegisteredServerInfo
                    {
                        Id = x.Id,
                        Address = x.Address,
                        ComputerName = x.ComputerName,
                        RegisteredDate = x.RegisteredDate,
                        LastNotifiedDate = x.LastNotifiedDate,
                        IsActive = x.IsActive,
                        IsSchedulingPublisher = x.IsSchedulingPublisher,
                        IsStale = ServerModel.IsStaleAt(x.LastNotifiedDate, utcNow)
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read registered servers.");
                return [];
            }
        }

        private string GetCurrentServerRole()
        {
            try
            {
                return serverRoleAccessor.CurrentServerRole.ToString();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not determine the current server role.");
                return "Unknown";
            }
        }

        private SystemDateMigrationInfo GetMigrationInfo(TimeZoneInfo serverZone, DateTimeOffset utcNow)
        {
            var settings = migrationSettings.Value;
            var configured = settings.LocalServerTimeZone?.Trim() ?? string.Empty;
            TimeZoneInfo? configuredZone = null;

            if (configured.Length > 0)
            {
                try
                {
                    configuredZone = TimeZoneInfo.FindSystemTimeZoneById(configured);
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    configuredZone = null;
                }
            }

            var effectiveZone = configured.Length > 0 ? configuredZone : serverZone;
            var effectiveName = configured.Length > 0 ? configured : serverZone.Id;
            var databaseType = GetDatabaseType();
            var isSqlite = databaseType.StartsWith("SQLite", StringComparison.OrdinalIgnoreCase);

            return new SystemDateMigrationInfo
            {
                Enabled = settings.Enabled,
                ConfiguredTimeZone = configured,
                ConfiguredTimeZoneValid = configured.Length > 0 ? configuredZone is not null : null,
                EffectiveTimeZone = effectiveName,
                EffectiveTimeZoneMatchesServer = effectiveZone is not null
                    && (effectiveZone.HasSameRules(serverZone)
                        || (effectiveZone.BaseUtcOffset == serverZone.BaseUtcOffset
                            && effectiveZone.GetUtcOffset(utcNow) == serverZone.GetUtcOffset(utcNow))),
                EffectiveTimeZoneIsUtc = MigrationUtcIdentifiers.Contains(effectiveName, StringComparer.OrdinalIgnoreCase)
                    || (effectiveZone is not null && effectiveZone.BaseUtcOffset == TimeSpan.Zero && !effectiveZone.SupportsDaylightSavingTime),
                DatabaseType = databaseType,
                UsesBaseOffsetOnly = isSqlite,
                UpgradeComplete = runtimeState.Level == Umbraco.Cms.Core.RuntimeLevel.Run
                    && string.Equals(runtimeState.CurrentMigrationState, runtimeState.FinalMigrationState, StringComparison.OrdinalIgnoreCase),
                CurrentMigrationState = runtimeState.CurrentMigrationState ?? string.Empty,
                FinalMigrationState = runtimeState.FinalMigrationState ?? string.Empty
            };
        }

        private string GetDatabaseType()
        {
            try
            {
                // NPoco types are named e.g. SQLiteDatabaseType or SqlServer2012DatabaseType.
                var name = databaseService.GetDatabaseType().GetType().Name;
                return name.EndsWith("DatabaseType", StringComparison.Ordinal) ? name[..^"DatabaseType".Length] : name;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not determine the database type.");
                return string.Empty;
            }
        }

        private static (string IanaId, string WindowsId) GetZoneIds(TimeZoneInfo zone)
        {
            if (zone.HasIanaId)
            {
                return (zone.Id, TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var windowsId) ? windowsId : string.Empty);
            }

            return (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId) ? ianaId : string.Empty, zone.Id);
        }

        /// <summary>
        /// Finds the next point within roughly a year at which the zone's UTC offset changes (e.g. a daylight saving switch).
        /// </summary>
        private static DateTimeOffset? GetNextTransition(TimeZoneInfo zone, DateTimeOffset utcNow)
        {
            if (!zone.SupportsDaylightSavingTime)
            {
                return null;
            }

            var current = zone.GetUtcOffset(utcNow);
            var start = new DateTimeOffset(utcNow.UtcDateTime.Date, TimeSpan.Zero);

            for (var day = 1; day <= 400; day++)
            {
                var candidate = start.AddDays(day);
                if (zone.GetUtcOffset(candidate) == current)
                {
                    continue;
                }

                // The change happened in the preceding 24 hours: narrow it down to the minute.
                var low = candidate.AddDays(-1);
                var high = candidate;
                while (high - low > TimeSpan.FromMinutes(1))
                {
                    var mid = low + TimeSpan.FromTicks((high - low).Ticks / 2);
                    if (zone.GetUtcOffset(mid) == current)
                    {
                        low = mid;
                    }
                    else
                    {
                        high = mid;
                    }
                }

                var transition = new DateTimeOffset(high.UtcTicks - (high.UtcTicks % TimeSpan.TicksPerMinute), TimeSpan.Zero);
                return transition > utcNow ? transition : null;
            }

            return null;
        }

        private static DateTime GetProcessStartedUtc()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.StartTime.ToUniversalTime();
            }
            catch
            {
                // Deliberate: StartTime can throw in restricted hosting environments.
                return FallbackStartedUtc;
            }
        }

        internal static string FormatOffset(TimeSpan offset)
            => $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}";
    }
}
