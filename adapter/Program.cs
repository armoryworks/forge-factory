using Forge.Factory.Adapter;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient<ForgeApiClient>(client =>
{
    var baseUrl = builder.Configuration["ForgeApi:BaseUrl"] ?? "http://127.0.0.1:5000";
    client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddSingleton<ColdPathLoader>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<SimHubBroadcaster>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Live-delta hub per adapter-contract-v0.md §3 — push-only, adapter -> Godot client.
// Stub: wired and reachable, but nothing on the adapter side drives it yet (no sim
// tick loop lives here; see SimHub.cs).
app.MapHub<SimHub>("/hubs/sim");

// Cold-path trigger. Not called per-tick — the Godot scaffold reads the JSON file this
// writes, it never calls this endpoint itself (B3: sim owns no HTTP/PG access).
app.MapPost("/cold-load", async (ColdPathLoader loader, CancellationToken ct) =>
{
    var content = await loader.RunAsync(ct);
    return Results.Ok(new { content.Items.Count, RecipesCount = content.Recipes.Count, content.Gaps });
});

// Checkpoint stub (D10: HTTP-only persistence, no adapter-owned Postgres). Real callers
// batch at ~1/s from the sim's checkpoint cadence; this endpoint exists so that cadence
// can be exercised/tested without wiring a timer for the slice. Fires sim.checkpointed
// on the live-delta hub (§3) so a connected client can correlate — stub tick value
// (no sim tick loop lives here yet; see SimHub.cs).
app.MapPost("/checkpoint", async (ForgeApiClient forge, SimHubBroadcaster hubBroadcaster, CheckpointDelta body, CancellationToken ct) =>
{
    await forge.WriteCheckpointDeltaAsync(body.PartId, body.Delta, body.LocationId, body.Reason ?? "sim checkpoint", ct);
    await hubBroadcaster.SimCheckpointedAsync(tick: 0, ct);
    return Results.NoContent();
});

app.Run();

record CheckpointDelta(int PartId, decimal Delta, int? LocationId, string? Reason);
