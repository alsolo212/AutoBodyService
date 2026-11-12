using System.Text.Json.Serialization;
using AutoBodyService.Infrastructure;
using AutoBodyService.Api.Security;
using AutoBodyService.Api.Configuration;
using Microsoft.AspNetCore.Mvc;
using AutoBodyService.Infrastructure.Identity;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddInfrastructure(builder.Configuration.GetConnectionString("AutoBodyService"));
builder.Services.AddApiSecurity();
builder.Services.Configure<PublicContactsOptions>(builder.Configuration.GetSection("PublicContacts"));
builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

if (args.Contains("--initialize-superadmin", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var initializer = scope.ServiceProvider.GetRequiredService<SuperAdminInitializer>();
    await initializer.InitializeAsync(
        builder.Configuration["InitialSuperAdmin:Name"],
        builder.Configuration["InitialSuperAdmin:Phone"],
        builder.Configuration["InitialSuperAdmin:Password"]);
    return;
}

// Configure the HTTP request pipeline.
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true &&
        !context.Request.Path.Equals("/api/auth/session", StringComparison.OrdinalIgnoreCase) &&
        !context.Request.Path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase) &&
        !context.Request.Path.Equals("/api/auth/antiforgery", StringComparison.OrdinalIgnoreCase) &&
        !context.Request.Path.Equals("/api/auth/change-password", StringComparison.OrdinalIgnoreCase) &&
        !(HttpMethods.IsGet(context.Request.Method) && (context.Request.Path.Equals("/api/public/contacts", StringComparison.OrdinalIgnoreCase) || context.Request.Path.Equals("/api/public/contacts/", StringComparison.OrdinalIgnoreCase) || context.Request.Path.Equals("/api/prices", StringComparison.OrdinalIgnoreCase) || context.Request.Path.Equals("/api/prices/", StringComparison.OrdinalIgnoreCase))))
    {
        var users = context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var user = await users.GetUserAsync(context.User);
        if (user is null || user.IsBlocked || user.MustChangePassword)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    await next();
});
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program { }
