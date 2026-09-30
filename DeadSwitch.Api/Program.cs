var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

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

app.Run();