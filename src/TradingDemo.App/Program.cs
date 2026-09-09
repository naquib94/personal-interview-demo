using System.Text.Json;
using System.Text.Json.Serialization;
using TradingDemo.App.Domain;
using TradingDemo.App.Endpoints;

// =========================================================================================
// Trading Platform Demo - application under test
// =========================================================================================
// This exists so the automation suite has something real to drive. It is intentionally the
// smallest thing that produces genuine testing problems: authentication, validation, business
// rules, pagination, an error envelope, and a database whose state can be verified
// independently of the API.
//
// It is not the showcase. The framework in src/QaFramework.* is. See README.md.
// =========================================================================================

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

// camelCase in, camelCase out, and enums as strings. Fixing the serialisation contract in one
// place means the test framework configures its deserialiser identically, once.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

string databasePath = builder.Configuration["Demo:DatabasePath"]
    ?? Path.Combine(AppContext.BaseDirectory, "trading-demo.db");
string scriptRoot = Path.Combine(AppContext.BaseDirectory, "Database");

builder.Services.AddSingleton(new DemoDatabase(databasePath, scriptRoot));
builder.Services.AddSingleton<TokenService>();

WebApplication app = builder.Build();

app.Services.GetRequiredService<DemoDatabase>().Initialise();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapAuthEndpoints();
app.MapTradingEndpoints();

// -----------------------------------------------------------------------------------------
// Test-support surface.
// -----------------------------------------------------------------------------------------
// Two endpoints that exist purely for the benefit of the test suite. Shipping a deliberate,
// documented test hook is a legitimate engineering decision - it is what lets a scenario
// guarantee its own preconditions in milliseconds instead of clicking through setup or
// tolerating order-dependent tests.
//
// The honest trade-off: a hook like this must never reach production. In a real system it
// would be gated behind an environment check or compiled out entirely. That gate is
// represented here by Demo:EnableTestSupport, which the automation profile switches on.
if (app.Configuration.GetValue("Demo:EnableTestSupport", true))
{
    app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).WithName("Health");

    app.MapPost("/test-support/reset", (DemoDatabase db) =>
    {
        db.Reset();
        return Results.Ok(new { status = "reset" });
    })
    .WithName("ResetDatabase");
}

app.Run();

/// <summary>
/// Marker type so integration hosts can reference this assembly by name.
/// </summary>
public sealed partial class TradingDemoApp;
