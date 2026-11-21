using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AutoBodyService.Application.Appointments;
using AutoBodyService.Application.Audit;
using AutoBodyService.Domain.Appointments;
using AutoBodyService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Appointments;

public sealed class RepairPlanning(ApplicationDbContext db, IAdminAuditWriter audit, TimeProvider clock) : IRepairPlanning
{
    private sealed record Row(Guid Id, AppointmentStatus Status, uint Version, DateTimeOffset CreatedAt, string Name, string Phone,
        string Make, string Model, DateOnly Start, DateOnly Finish, RepairPeriod Effective);
    private sealed record State(DateOnly Today, int? Capacity, int Actual, IReadOnlyList<Row> Rows, IReadOnlyList<RepairPause> Pauses);

    private async Task<State> LoadAsync(CancellationToken ct)
    {
        var today = RepairCapacityStore.LocalDate(clock.GetUtcNow());
        var capacity = (await db.RepairCapacity.AsNoTracking().SingleAsync(ct)).Workplaces;
        var actual = await RepairCapacityStore.ActualCountAsync(db, ct);
        var pauses = await db.RepairPauses.AsNoTracking().Where(p => p.EndedAtUtc == null).ToListAsync(ct);
        var pauseByOrder = pauses.ToDictionary(p => p.AppointmentId);
        var records = await (from a in db.Appointments.AsNoTracking()
                             join u in db.Users.AsNoTracking() on a.UserId equals u.Id into users
                             from u in users.DefaultIfEmpty()
                             where (a.Status == AppointmentStatus.Scheduled || a.Status == AppointmentStatus.InProgress || a.Status == AppointmentStatus.Paused) && a.RepairPlan != null
                             select new { a.Id, a.Status, Version = EF.Property<uint>(a, "Version"), a.CreatedAtUtc,
                                 Name = u != null ? u.Name : a.CustomerName!, Phone = u != null ? u.PhoneNormalized : a.CustomerPhone!,
                                 a.Make, a.Model, a.RepairPlan!.PlannedStartUtc, a.RepairPlan.EstimatedFinishUtc }).ToListAsync(ct);
        var rows = records.Select(r =>
        {
            var start = RepairCapacityStore.LocalDate(r.PlannedStartUtc); var finish = RepairCapacityStore.LocalDate(r.EstimatedFinishUtc);
            var effectiveStart = start; var effectiveFinish = finish;
            if (r.Status == AppointmentStatus.InProgress && effectiveFinish < today) effectiveFinish = today;
            if (r.Status == AppointmentStatus.Paused && pauseByOrder.TryGetValue(r.Id, out var pause))
            {
                if (pause.WorkplaceReleased) effectiveStart = pause.ExpectedReturn;
                if (effectiveFinish < pause.ExpectedReturn) effectiveFinish = pause.ExpectedReturn;
                if (effectiveFinish < today) effectiveFinish = today;
            }
            return new Row(r.Id, r.Status, r.Version, r.CreatedAtUtc, r.Name, r.Phone, r.Make, r.Model, start, finish, new(effectiveStart, effectiveFinish));
        }).ToArray();
        return new(today, capacity, actual, rows, pauses);
    }

    private static RepairLoad ToLoad(Row row) => new(row.Id, row.Status, row.Effective, row.CreatedAt);
    private static RepairPlanningOrder Map(Row row, DateOnly? alternative = null) =>
        new(row.Id, row.Name, row.Phone, row.Make, row.Model, row.Start, row.Finish, alternative, row.Version);

    private static IReadOnlyList<RepairPlanningOrder> Conflicts(State state)
    {
        if (state.Capacity is not int capacity) return [];
        var loads = state.Rows.Select(ToLoad).ToArray();
        var conflictIds = RepairLoadPlanner.Conflicts(loads, capacity);
        return state.Rows.Where(r => conflictIds.Contains(r.Id)).Select(r =>
        {
            var duration = r.Finish.DayNumber - r.Start.DayNumber;
            var earliest = r.Start > state.Today ? r.Start : state.Today;
            var others = state.Rows.Where(x => x.Id != r.Id).Select(x => x.Effective).ToArray();
            var alternative = RepairAvailability.Nearest(earliest, state.Today.AddYears(1), duration, others, capacity);
            return Map(r, alternative);
        }).ToArray();
    }

    public async Task<RepairPlanningOverview> OverviewAsync(CancellationToken cancellationToken)
    {
        var state = await LoadAsync(cancellationToken);
        Row? candidate = null;
        if (state.Capacity is int capacity && state.Actual < capacity)
        {
            foreach (var row in state.Rows.Where(r => r.Status == AppointmentStatus.Scheduled && r.Start > state.Today)
                         .OrderBy(r => r.Start).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id))
            {
                var duration = row.Finish.DayNumber - row.Start.DayNumber;
                if (duration <= 0 || (long)state.Today.DayNumber + duration > DateOnly.MaxValue.DayNumber) continue;
                if (RepairAvailability.Fits(new(state.Today, state.Today.AddDays(duration)), state.Rows.Where(x => x.Id != row.Id).Select(x => x.Effective), capacity)) { candidate = row; break; }
            }
        }
        return new(Conflicts(state), candidate is null ? null : Map(candidate, state.Today));
    }

    public async Task<RepairPauseOffer?> PauseOfferAsync(Guid pauseId, CancellationToken cancellationToken)
    {
        var state = await LoadAsync(cancellationToken);
        var pause = state.Pauses.SingleOrDefault(p => p.Id == pauseId);
        if (pause is null || !pause.WorkplaceReleased || !state.Rows.Any(r => r.Id == pause.AppointmentId && r.Status == AppointmentStatus.Paused)) return null;
        var declines = (await db.RepairOfferDeclines.AsNoTracking().Where(d => d.PauseId == pauseId).Select(d => d.AppointmentId).ToListAsync(cancellationToken)).ToHashSet();
        RepairLoad? offer = null;
        if (state.Capacity is int capacity && state.Actual < capacity)
            offer = RepairLoadPlanner.FirstPauseOffer(state.Rows.Select(ToLoad).ToArray(), pause.AppointmentId, state.Today, pause.ExpectedReturn, capacity, declines);
        return new(pause.Id, pause.ExpectedReturn, offer is null ? null : Map(state.Rows.Single(r => r.Id == offer.Id), state.Today));
    }

    public async Task<AdminAppointmentResult> DeclinePauseOfferAsync(Guid pauseId, Guid candidateId, string actorId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RepairCapacityStore.LockAsync(db, cancellationToken);
        var offer = await PauseOfferAsync(pauseId, cancellationToken);
        if (offer?.Candidate?.Id != candidateId) return AdminAppointmentResult.Conflict;
        db.RepairOfferDeclines.Add(new RepairOfferDecline(pauseId, candidateId, actorId, clock.GetUtcNow()));
        audit.RecordRepairOfferDeclined(actorId, pauseId, candidateId);
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return AdminAppointmentResult.Success;
    }

    public async Task<RepairDelayPreview?> PreviewDelayAsync(Guid id, DateOnly finish, CancellationToken cancellationToken)
    {
        var state = await LoadAsync(cancellationToken);
        var row = state.Rows.SingleOrDefault(r => r.Id == id);
        if (row is null || row.Status is not (AppointmentStatus.InProgress or AppointmentStatus.Paused) || finish <= row.Finish) return null;
        var effectiveEnd = finish > row.Effective.Finish ? finish : row.Effective.Finish;
        var changed = state with { Rows = state.Rows.Select(r => r.Id == id ? r with { Finish = finish, Effective = new(r.Effective.Start, effectiveEnd) } : r).ToArray() };
        var before = Conflicts(state).Select(c => c.Id).ToHashSet();
        var affected = Conflicts(changed).Where(c => c.Id != id && (!before.Contains(c.Id) ||
            changed.Rows.Any(r => r.Id == c.Id && r.Effective.Start <= effectiveEnd && r.Effective.Finish > row.Effective.Finish))).ToArray();
        return new(id, finish, Token(state, id, finish), affected);
    }

    public async Task<bool> VerifyDelayAsync(Guid id, DateOnly finish, string token, CancellationToken cancellationToken)
    {
        var state = await LoadAsync(cancellationToken);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Token(state, id, finish)), Encoding.ASCII.GetBytes(token));
    }

    private static string Token(State state, Guid id, DateOnly finish)
    {
        var text = new StringBuilder().Append(state.Today.ToString("O", CultureInfo.InvariantCulture)).Append('|').Append(state.Capacity).Append('|').Append(id).Append('|').Append(finish.ToString("O", CultureInfo.InvariantCulture));
        foreach (var row in state.Rows.OrderBy(r => r.Id)) text.Append('|').Append(row.Id).Append(':').Append(row.Version).Append(':').Append((int)row.Status).Append(':').Append(row.Effective.Start.DayNumber).Append(':').Append(row.Effective.Finish.DayNumber);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
