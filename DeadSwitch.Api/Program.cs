using System.Diagnostics;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () =>
{
    return Results.Ok(new
    {
        status = "UP",
        service = "DeadSwitch"
    });
});

app.MapGet("/check", async (string url, IHttpClientFactory httpClientFactory) =>
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