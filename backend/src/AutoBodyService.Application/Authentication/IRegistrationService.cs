namespace AutoBodyService.Application.Authentication;

public interface IRegistrationService
{
    Task<bool> RegisterAsync(string name, string phone, string password, CancellationToken cancellationToken);
}
