using System.Text.Json.Serialization;
using AutoBodyService.Infrastructure;
using AutoBodyService.Infrastructure.Identity;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddInfrastructure(builder.Configuration.GetConnectionString("AutoBodyService"));
builder.Services.AddControllers()
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

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
