using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoBodyService.Api.Configuration;
using AutoBodyService.Application.Contacts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoBodyService.IntegrationTests;

public sealed class PublicContactsTests
{
    [Theory]
    [InlineData("(000) 000-00-00", "+380000000000")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("", null)]
    public async Task ReturnsOnlyValidatedPublicValues(string phone, string? expected)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IWorkshopContacts, EmptyContacts>());
            services.PostConfigure<PublicContactsOptions>(options =>
            {
                options.Phone = phone;
                options.Address = " Тестова адреса ";
                options.WorkingHours = " За домовленістю ";
            });
        }));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var response = await client.GetAsync("/api/public/contacts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "address", "phone", "workingHours" }, json.EnumerateObject().Select(p => p.Name).OrderBy(x => x));
        Assert.Equal(expected, json.GetProperty("phone").GetString());
        Assert.Equal("Тестова адреса", json.GetProperty("address").GetString());
        Assert.Equal("За домовленістю", json.GetProperty("workingHours").GetString());
    }

    private sealed class EmptyContacts : IWorkshopContacts
    {
        public Task<WorkshopContacts> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new WorkshopContacts(null, null, 0));
        public Task<WorkshopContacts?> UpdateAsync(string phone, string address, int version, string actorId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
