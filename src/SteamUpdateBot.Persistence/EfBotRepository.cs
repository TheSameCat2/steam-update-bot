using Microsoft.EntityFrameworkCore;
using SteamUpdateBot.Core.Contracts;
using SteamUpdateBot.Core.Domain;

namespace SteamUpdateBot.Persistence;

public sealed class EfBotRepository : IBotRepository
{
    private readonly IDbContextFactory<BotDbContext> _contextFactory;

    public EfBotRepository(IDbContextFactory<BotDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<MonitoredGame?> GetGameAsync(uint appId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.MonitoredGames
            .AsNoTracking()
            .SingleOrDefaultAsync(game => game.AppId == appId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> GetGameCountAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.MonitoredGames.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryAddGameAsync(
        MonitoredGame game,
        IReadOnlyList<SteamAnnouncement> suppressedAnnouncements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(suppressedAnnouncements);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await context.MonitoredGames.AnyAsync(existing => existing.AppId == game.AppId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        context.MonitoredGames.Add(game);
        var baseline = suppressedAnnouncements
            .Where(announcement => announcement.AppId == game.AppId && !string.IsNullOrWhiteSpace(announcement.Gid))
            .GroupBy(announcement => announcement.Gid, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(announcement => announcement.PublishedAtUtc).First())
            .OrderBy(announcement => announcement.PublishedAtUtc)
            .ThenBy(announcement => announcement.Gid, StringComparer.Ordinal);

        foreach (var announcement in baseline)
        {
            context.Announcements.Add(CreateAnnouncementRecord(
                game,
                announcement,
                AnnouncementDeliveryState.Suppressed,
                game.AddedAtUtc));
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RemoveGameAsync(uint appId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var game = await context.MonitoredGames.SingleOrDefaultAsync(game => game.AppId == appId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return false;
        }

        context.MonitoredGames.Remove(game);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<PagedResult<MonitoredGame>> ListGamesAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var normalizedPage = Math.Max(page, 1);
        var normalizedPageSize = Math.Clamp(pageSize, 1, 100);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = context.MonitoredGames.AsNoTracking().OrderBy(game => game.Name).ThenBy(game => game.AppId);
        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new PagedResult<MonitoredGame>(items, normalizedPage, normalizedPageSize, totalCount);
    }

    public async Task<IReadOnlyList<MonitoredGame>> GetDueGamesAsync(
        DateTimeOffset nowUtc,
        int maxGames,
        CancellationToken cancellationToken)
    {
        if (maxGames <= 0)
        {
            return [];
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.MonitoredGames
            .AsNoTracking()
            .Where(game => game.NextPollUtc <= nowUtc)
            .OrderBy(game => game.NextPollUtc)
            .ThenBy(game => game.AppId)
            .Take(maxGames)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PollCommitResult> CommitPollAsync(
        uint appId,
        DateTimeOffset pollStartedUtc,
        DateTimeOffset nextPollUtc,
        IReadOnlyList<SteamAnnouncement> announcements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(announcements);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var game = await context.MonitoredGames.SingleOrDefaultAsync(candidate => candidate.AppId == appId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return new PollCommitResult(0, false);
        }

        var candidates = announcements
            .Where(announcement => announcement.AppId == appId && !string.IsNullOrWhiteSpace(announcement.Gid))
            .GroupBy(announcement => announcement.Gid, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(announcement => announcement.PublishedAtUtc).First())
            .OrderBy(announcement => announcement.PublishedAtUtc)
            .ThenBy(announcement => announcement.Gid, StringComparer.Ordinal)
            .ToList();

        var candidateGids = candidates.Select(announcement => announcement.Gid).ToArray();
        var knownGids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gidBatch in candidateGids.Chunk(900))
        {
            var existingGids = await context.Announcements
                .AsNoTracking()
                .Where(announcement => announcement.AppId == appId && gidBatch.Contains(announcement.SteamGid))
                .Select(announcement => announcement.SteamGid)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            knownGids.UnionWith(existingGids);
        }

        var queuedCount = 0;
        foreach (var announcement in candidates)
        {
            if (!knownGids.Add(announcement.Gid))
            {
                continue;
            }

            context.Announcements.Add(CreateAnnouncementRecord(
                game,
                announcement,
                AnnouncementDeliveryState.Pending,
                pollStartedUtc));
            queuedCount++;
        }

        game.LastPollAttemptUtc = pollStartedUtc;
        game.LastPollSuccessUtc = pollStartedUtc;
        game.NextPollUtc = nextPollUtc;
        game.ConsecutiveFailures = 0;
        game.LastError = null;
        if (pollStartedUtc > game.ScanWatermarkUtc)
        {
            game.ScanWatermarkUtc = pollStartedUtc;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PollCommitResult(queuedCount, true);
    }

    public async Task RecordPollFailureAsync(
        uint appId,
        DateTimeOffset attemptedAtUtc,
        DateTimeOffset nextPollUtc,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var game = await context.MonitoredGames.SingleOrDefaultAsync(candidate => candidate.AppId == appId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return;
        }

        game.LastPollAttemptUtc = attemptedAtUtc;
        game.NextPollUtc = nextPollUtc;
        game.ConsecutiveFailures++;
        game.LastError = Truncate(failureMessage, 1_000);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AnnouncementRecord>> GetDueAnnouncementsAsync(
        DateTimeOffset nowUtc,
        int maxCount,
        CancellationToken cancellationToken)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pendingAnnouncements = await context.Announcements
            .AsNoTracking()
            .Where(announcement =>
                announcement.DeliveryState == AnnouncementDeliveryState.Pending
                || announcement.DeliveryState == AnnouncementDeliveryState.Publishing)
            .OrderBy(announcement => announcement.AppId)
            .ThenBy(announcement => announcement.PublishedAtUtc)
            .ThenBy(announcement => announcement.SteamGid)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var dueAnnouncements = new List<AnnouncementRecord>();
        uint? currentAppId = null;
        var appBlocked = false;
        foreach (var announcement in pendingAnnouncements)
        {
            if (currentAppId != announcement.AppId)
            {
                currentAppId = announcement.AppId;
                appBlocked = false;
            }

            if (appBlocked)
            {
                continue;
            }

            if (announcement.DeliveryState == AnnouncementDeliveryState.Publishing)
            {
                dueAnnouncements.Add(announcement);
                appBlocked = true;
                continue;
            }

            // Preserve publication ordering per game: a retrying announcement
            // blocks later ones for that app only.
            if (announcement.NextAttemptUtc > nowUtc)
            {
                appBlocked = true;
                continue;
            }

            dueAnnouncements.Add(announcement);
        }

        return dueAnnouncements
            .OrderBy(announcement => announcement.PublishedAtUtc)
            .ThenBy(announcement => announcement.AppId)
            .ThenBy(announcement => announcement.SteamGid)
            .Take(maxCount)
            .ToList();
    }

    public async Task<PublishClaimResult> TryClaimForPublishAsync(
        uint appId,
        string steamGid,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var announcement = await context.Announcements.SingleOrDefaultAsync(
                candidate => candidate.AppId == appId && candidate.SteamGid == steamGid,
                cancellationToken)
            .ConfigureAwait(false);
        if (announcement is null)
        {
            return new PublishClaimResult(PublishClaimOutcome.NotFound);
        }

        switch (announcement.DeliveryState)
        {
            case AnnouncementDeliveryState.Delivered:
            case AnnouncementDeliveryState.Abandoned:
            case AnnouncementDeliveryState.Suppressed:
                return new PublishClaimResult(PublishClaimOutcome.AlreadyComplete, announcement.DiscordMessageId);
            case AnnouncementDeliveryState.Publishing:
                return new PublishClaimResult(PublishClaimOutcome.AlreadyPublishing, announcement.DiscordMessageId);
            case AnnouncementDeliveryState.Pending:
                announcement.DeliveryState = AnnouncementDeliveryState.Publishing;
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new PublishClaimResult(PublishClaimOutcome.Claimed);
            default:
                return new PublishClaimResult(PublishClaimOutcome.NotFound);
        }
    }

    public async Task RecordPublishedMessageIdAsync(
        uint appId,
        string steamGid,
        string discordMessageId,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var announcement = await context.Announcements.SingleOrDefaultAsync(
                candidate => candidate.AppId == appId && candidate.SteamGid == steamGid,
                cancellationToken)
            .ConfigureAwait(false);
        if (announcement is null || announcement.DeliveryState != AnnouncementDeliveryState.Publishing)
        {
            return;
        }

        announcement.DiscordMessageId = Truncate(discordMessageId, 64);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkDeliveredAsync(
        uint appId,
        string steamGid,
        string discordMessageId,
        DateTimeOffset deliveredAtUtc,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var announcement = await context.Announcements.SingleOrDefaultAsync(
                candidate => candidate.AppId == appId && candidate.SteamGid == steamGid,
                cancellationToken)
            .ConfigureAwait(false);
        if (announcement is null
            || announcement.DeliveryState is not (AnnouncementDeliveryState.Pending or AnnouncementDeliveryState.Publishing))
        {
            return;
        }

        announcement.DeliveryState = AnnouncementDeliveryState.Delivered;
        announcement.DeliveredAtUtc = deliveredAtUtc;
        announcement.DiscordMessageId = Truncate(discordMessageId, 64);
        announcement.LastError = null;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordDeliveryFailureAsync(
        uint appId,
        string steamGid,
        DateTimeOffset nextAttemptUtc,
        string failureMessage,
        CancellationToken cancellationToken,
        bool incrementAttempt = true)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var announcement = await context.Announcements.SingleOrDefaultAsync(
                candidate => candidate.AppId == appId && candidate.SteamGid == steamGid,
                cancellationToken)
            .ConfigureAwait(false);
        if (announcement is null
            || announcement.DeliveryState is not (AnnouncementDeliveryState.Pending or AnnouncementDeliveryState.Publishing))
        {
            return;
        }

        announcement.DeliveryState = AnnouncementDeliveryState.Pending;
        if (incrementAttempt)
        {
            announcement.AttemptCount++;
        }

        announcement.NextAttemptUtc = nextAttemptUtc;
        announcement.LastError = Truncate(failureMessage, 1_000);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkDeliveryAbandonedAsync(
        uint appId,
        string steamGid,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var announcement = await context.Announcements.SingleOrDefaultAsync(
                candidate => candidate.AppId == appId && candidate.SteamGid == steamGid,
                cancellationToken)
            .ConfigureAwait(false);
        if (announcement is null
            || announcement.DeliveryState is not (AnnouncementDeliveryState.Pending or AnnouncementDeliveryState.Publishing))
        {
            return;
        }

        announcement.AttemptCount++;
        announcement.DeliveryState = AnnouncementDeliveryState.Abandoned;
        announcement.LastError = Truncate(failureMessage, 1_000);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotStatusSnapshot> GetStatusAsync(bool discordConnected, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var gameCount = await context.MonitoredGames.CountAsync(cancellationToken).ConfigureAwait(false);
            var pendingCount = await context.Announcements
                .CountAsync(
                    announcement => announcement.DeliveryState == AnnouncementDeliveryState.Pending
                        || announcement.DeliveryState == AnnouncementDeliveryState.Publishing,
                    cancellationToken)
                .ConfigureAwait(false);
            var failedDeliveryCount = await context.Announcements
                .CountAsync(
                    announcement => announcement.DeliveryState == AnnouncementDeliveryState.Pending && announcement.AttemptCount > 0,
                    cancellationToken)
                .ConfigureAwait(false);
            var abandonedDeliveryCount = await context.Announcements
                .CountAsync(
                    announcement => announcement.DeliveryState == AnnouncementDeliveryState.Abandoned,
                    cancellationToken)
                .ConfigureAwait(false);
            var lastSuccessfulPoll = await context.MonitoredGames
                .Where(game => game.LastPollSuccessUtc != null)
                .OrderByDescending(game => game.LastPollSuccessUtc)
                .Select(game => game.LastPollSuccessUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset? oldestSuccessfulPoll = gameCount == 0
                ? null
                : await context.MonitoredGames
                    .Select(game => game.LastPollSuccessUtc ?? game.AddedAtUtc)
                    .OrderBy(value => value)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
            var pollError = await context.MonitoredGames
                .Where(game => game.LastError != null)
                .OrderByDescending(game => game.LastPollAttemptUtc)
                .Select(game => game.LastError)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            var lastError = failedDeliveryCount > 0
                ? "One or more Discord announcement deliveries are retrying."
                : abandonedDeliveryCount > 0
                    ? "One or more Discord announcement deliveries were abandoned after repeated failures."
                    : pollError;
            return new BotStatusSnapshot(
                discordConnected,
                true,
                gameCount,
                pendingCount,
                failedDeliveryCount,
                lastSuccessfulPoll,
                oldestSuccessfulPoll,
                lastError,
                abandonedDeliveryCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new BotStatusSnapshot(discordConnected, false, 0, 0, 0, null, null, Truncate(exception.Message, 1_000));
        }
    }

    private static AnnouncementRecord CreateAnnouncementRecord(
        MonitoredGame game,
        SteamAnnouncement announcement,
        AnnouncementDeliveryState deliveryState,
        DateTimeOffset detectedAtUtc) =>
        new()
        {
            AppId = game.AppId,
            SteamGid = Truncate(announcement.Gid, 128),
            GameName = Truncate(game.Name, 256),
            HeaderImageUrl = TruncateNullable(game.HeaderImageUrl, 2_048),
            Title = Truncate(announcement.Title, 512),
            Url = Truncate(announcement.Url, 2_048),
            Content = Truncate(announcement.Content, 4_096),
            Author = TruncateNullable(announcement.Author, 256),
            PublishedAtUtc = announcement.PublishedAtUtc,
            DetectedAtUtc = detectedAtUtc,
            DeliveryState = deliveryState,
            AttemptCount = 0,
            NextAttemptUtc = detectedAtUtc,
        };

    private static string Truncate(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || maximumLength <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= maximumLength)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(maximumLength);
        var writtenLength = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (writtenLength + rune.Utf16SequenceLength > maximumLength)
            {
                break;
            }

            builder.Append(rune);
            writtenLength += rune.Utf16SequenceLength;
        }

        return builder.ToString();
    }

    private static string? TruncateNullable(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) ? null : Truncate(value, maximumLength);
}
