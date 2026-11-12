using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.IntegrationTests;

public sealed class ManualContactDomainTests
{
    [Fact]
    public void ManualContactRecordsValidPurposeActorAndUtcTime()
    {
        var id = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        foreach (var reason in Enum.GetValues<ManualContactReason>())
        {
            var item = new ManualContactEvent(id, reason, "actor", now);
            Assert.NotEqual(Guid.Empty, item.Id);
            Assert.Equal(id, item.AppointmentId);
            Assert.Equal(reason, item.Reason);
            Assert.Equal("actor", item.ActorId);
            Assert.Equal(now, item.RecordedAtUtc);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManualContactEvent(Guid.Empty, ManualContactReason.Agreement, "actor", now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManualContactEvent(id, (ManualContactReason)99, "actor", now));
        Assert.Throws<ArgumentException>(() => new ManualContactEvent(id, ManualContactReason.Agreement, " ", now));
        Assert.Throws<ArgumentException>(() => new ManualContactEvent(id, ManualContactReason.Agreement, "actor", now.ToOffset(TimeSpan.FromHours(2))));
    }
}
