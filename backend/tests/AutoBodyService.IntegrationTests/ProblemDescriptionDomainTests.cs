using AutoBodyService.Domain.Appointments;

namespace AutoBodyService.IntegrationTests;

public sealed class ProblemDescriptionDomainTests
{
    private static Appointment Create(string description) => new(Guid.NewGuid(), "owner", new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero),
        "1HGCM82633A123456", "Test", "Model", description, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("")]
    [InlineData("  \t ")]
    [InlineData("https://example.invalid/video.mp4")]
    [InlineData("https://example.invalid/video.mp4?items=a,b;size=large")]
    [InlineData("  (https://example.invalid/a.jpg), [https://example.invalid/b.mp4] !!! ")]
    [InlineData("Дивіться відео за посиланням: https://example.invalid/watch?v=1")]
    [InlineData("Смотрите видео и https://example.invalid/video.mp4")]
    [InlineData("Смотрите это видео: https://example.invalid/video.mp4")]
    [InlineData("See the photo: www.example.invalid/picture.jpg")]
    [InlineData("example.invalid/photo.jpg; example.invalid/video.mp4")]
    [InlineData("https://example.invalid/video.mp4 ... 123")]
    public void RejectsMissingOrLinkOnlyProblemDescriptions(string value)
    {
        Assert.False(ProblemDescriptionValidator.IsValid(value));
        Assert.Throws<ArgumentException>(() => Create(value));
    }

    [Theory]
    [InlineData("Вм’ятина")]
    [InlineData("Подряпина")]
    [InlineData("Бампер!")]
    [InlineData("Вм’ятина на дверях https://example.invalid/video.mp4")]
    [InlineData("https://example.invalid/photo.jpg,подряпина")]
    [InlineData("https://example.invalid/photo.jpg—подряпина")]
    [InlineData("Смотрите видео: помято крыло https://example.invalid/video.mp4")]
    [InlineData("Подряпина: https://example.invalid/a.jpg; https://example.invalid/b.mp4")]
    public void AcceptsWrittenProblemWithOrWithoutLinks(string value)
    {
        Assert.True(ProblemDescriptionValidator.IsValid(value));
        Assert.Equal(value.Trim(), Create(value).ProblemDescription);
    }
}
