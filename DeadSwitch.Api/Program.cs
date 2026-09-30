using DeadSwitch.Api.Requests;
using DeadSwitch.Application.Interfaces;
using DeadSwitch.Application.Services;
using DeadSwitch.Infrastructure.Data;
using DeadSwitch.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

builder.Services.AddDbContext<DeadSwitchDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DeadSwitchDatabase")));

builder.Services.AddScoped<IMonitoredServiceRepository, MonitoredServiceRepository>();
builder.Services.AddScoped<MonitoredServiceService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Endpoint 1: Health
app.MapGet("/health", () =>
{
    return Results.Ok(new
    {
        status = "UP",
        service = "DeadSwitch"
    });
});

// Endpoint 2: Register service
app.MapPost("/services", async (
    RegisterServiceRequest request,
    MonitoredServiceService service) =>
{
    var monitoredService = await service.RegisterAsync(
        request.Name,
        request.Url);

    return Results.Ok(monitoredService);
});

// Endpoint 3: Get all registered services
app.MapGet("/services", async (
    MonitoredServiceService service) =>
{
    var services = await service.GetAllAsync();

    return Results.Ok(services);
});

// Endpoint 4: Check URL
app.MapGet("/check", async (
    string url,
    IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient();
    var stopwatch = Stopwatch.StartNew();

    try
    {
        var response = await client.GetAsync(url);
        stopwatch.Stop();

        return Results.Ok(new
        {
            responseTimeMs = stopwatch.ElapsedMilliseconds,
            url = url,
            status = response.IsSuccessStatusCode ? "UP" : "DOWN",
            statusCode = (int)response.StatusCode
        });
    }
    catch
    {
        stopwatch.Stop();

        return Results.Ok(new
        {
            url = url,
            status = "DOWN",
            statusCode = (int?)null,
            responseTimeMs = stopwatch.ElapsedMilliseconds
        });
    }
});

app.Run();