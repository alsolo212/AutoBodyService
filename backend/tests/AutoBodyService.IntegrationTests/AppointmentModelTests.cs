using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.IntegrationTests;

public sealed class AppointmentModelTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Inspection = new(2026, 1, 2, 10, 0, 0, TimeSpan.Zero);
    private static Appointment Create(string vin = "1HGCM82633A123456", string make = "Test", string model = "Model", string problem = "Problem") =>
        new(Guid.NewGuid(), "owner", Inspection, vin, make, model, problem, Created);

    [Fact]
    public void NewAppointmentCreatesInitialStatusEventAndTrimsInputs()
    {
        var appointment = Create(" 1HGCM82633A123456 ", " Test ", " Model ", " Problem ");
        Assert.Equal("1HGCM82633A123456", appointment.Vin);
        Assert.Equal("Test", appointment.Make);
        Assert.Equal("Model", appointment.Model);
        Assert.Equal("Problem", appointment.ProblemDescription);
        Assert.Equal(AppointmentStatus.AwaitingInspection, appointment.Status);
        var history = Assert.Single(appointment.StatusEvents);
        Assert.NotEqual(Guid.Empty, history.Id);
        Assert.Equal(appointment.Id, history.AppointmentId);
        Assert.Null(history.FromStatus);
        Assert.Equal(AppointmentStatus.AwaitingInspection, history.ToStatus);
        Assert.Equal("owner", history.ActorId);
        Assert.Equal(Created, history.ChangedAtUtc);
        Assert.Null(history.Reason);
    }

    [Theory]
    [InlineData("1HGCM82633A12345")]
    [InlineData("1HGCM82633A1234567")]
    [InlineData("1HGCM82633A12345I")]
    [InlineData("1HGCM82633A12345O")]
    [InlineData("1HGCM82633A12345Q")]
    [InlineData("1HGCM82633A12345a")]
    [InlineData("1HGCM82633A12345-")]
    [InlineData("1HGCM82633A12345А")]
    public void InvalidVinIsRejected(string vin) => Assert.Throws<ArgumentException>(() => Create(vin));

    [Fact]
    public void MissingFieldsAndNonUtcMomentsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(make: " \t "));
        Assert.Throws<ArgumentException>(() => Create(model: " \n "));
        Assert.Throws<ArgumentException>(() => Create(problem: "   "));
        Assert.Throws<ArgumentException>(() => new Appointment(Guid.NewGuid(), "owner", Inspection.ToOffset(TimeSpan.FromHours(2)), "1HGCM82633A123456", "Test", "Model", "Problem", Created));
        Assert.Throws<ArgumentException>(() => new Appointment(Guid.NewGuid(), "owner", Inspection, "1HGCM82633A123456", "Test", "Model", "Problem", Created.ToOffset(TimeSpan.FromHours(2))));
    }
}
