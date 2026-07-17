using Forge.Factory.Adapter;
using Forge.Sim;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient<ForgeApiClient>(client =>
{
    var baseUrl = builder.Configuration["ForgeApi:BaseUrl"] ?? "http://127.0.0.1:5000";
    client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddSingleton<ColdPathLoader>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<SimHubBroadcaster>();

// Content load, FAIL FAST. recipes-v0.toml is parsed directly by the sim core's loader (D14/D18),
// which runs the spec §2.4 rules. Invalid content must kill the process at startup rather than
// boot a sim running content nobody authored -- a validation error here is unrecoverable, and a
// half-valid factory is worse than no factory.
var contentPath = Path.GetFullPath(
    builder.Configuration["Sim:ContentPath"] ?? "../data/recipes-v0.toml",
    builder.Environment.ContentRootPath);

Content content;
try
{
    content = Content.Load(contentPath);
}
catch (Exception ex) when (ex is ContentValidationException or TomlParseException or FileNotFoundException)
{
    // Console, not ILogger: the host isn't built yet, and this must be legible in a crash log.
    Console.Error.WriteLine($"FATAL: could not load sim content from {contentPath}\n{ex.Message}");
    return 1;
}

builder.Services.AddSingleton(content);
builder.Services.AddSingleton<SimTickService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SimTickService>());

var app = builder.Build();

app.Logger.LogInformation("Loaded sim content from {Path}: {Items} items, {Recipes} recipes, tick_hz={TickHz}",
    contentPath, content.Items.Count, content.Recipes.Count, content.TickHz);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Curl-visible probe of real sim state. Read-only -- it does not advance the sim; the tick loop
// in SimTickService is the only thing that does.
app.MapGet("/sim/state", (SimTickService sim) => Results.Ok(sim.Snapshot()));

// Live-delta hub per adapter-contract-v0.md §3 — push-only, adapter -> Godot client.
// Driven for real by SimTickService, which hosts the sim core in this process.
app.MapHub<SimHub>("/hubs/sim");

// Cold-path trigger. Not called per-tick — the Godot scaffold reads the JSON file this
// writes, it never calls this endpoint itself (B3: sim owns no HTTP/PG access).
app.MapPost("/cold-load", async (ColdPathLoader loader, SimHubBroadcaster hubBroadcaster, ILoggerFactory lf, CancellationToken ct) =>
{
    try
    {
        var content = await loader.RunAsync(ct);
        return Results.Ok(new { content.Items.Count, RecipesCount = content.Recipes.Count, content.Gaps });
    }
    catch (Exception ex)
    {
        // §3's sim.error is specified for exactly this: "adapter-side fault (e.g. forge-api
        // unreachable during a cold-path fetch)". Tell connected clients rather than only
        // returning a 500 to whoever POSTed — the player's client is not the caller here.
        lf.CreateLogger("ColdPath").LogError(ex, "cold-path load failed");
        await hubBroadcaster.SimErrorAsync($"cold-path load failed: {ex.Message}", ct);
        return Results.Problem($"cold-path load failed: {ex.Message}");
    }
});

// Checkpoint endpoint (D10: HTTP-only persistence, no adapter-owned Postgres). Real callers
// batch at ~1/s from the sim's checkpoint cadence; this endpoint exists so that cadence
// can be exercised/tested without wiring a timer for the slice. D16 machinery is live-verified
// and reused as-is. Fires sim.checkpointed on the live-delta hub (§3) so a connected client can
// correlate — now carrying the real sim tick rather than a stub 0.
app.MapPost("/checkpoint", async (ForgeApiClient forge, SimHubBroadcaster hubBroadcaster, SimTickService sim, CheckpointDelta body, CancellationToken ct) =>
{
    await forge.WriteCheckpointDeltaAsync(body.PartId, body.Delta, body.LocationId, body.Reason ?? "sim checkpoint", ct);
    await hubBroadcaster.SimCheckpointedAsync(tick: (long)sim.World.Tick, ct);
    return Results.NoContent();
});

app.Run();
return 0;

record CheckpointDelta(int PartId, decimal Delta, int? LocationId, string? Reason);
