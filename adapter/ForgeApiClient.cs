using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Factory.Adapter;

// Owns the adapter's ONE forge credential (its own kiosk service identity — see
// adapter-contract-v0.md §1/§4). Player/game-operator sessions never reuse this
// token and never talk to forge-api directly (B3/B4: the adapter owns all forge
// access; Godot speaks HTTP+hub to the adapter only, never to forge-api or Postgres).
public sealed class ForgeApiClient(HttpClient http, IConfiguration config, ILogger<ForgeApiClient> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private string? _token;

    public async Task<string> LoginAsync(CancellationToken ct = default)
    {
        var barcode = config["ForgeApi:Barcode"] ?? throw new InvalidOperationException("ForgeApi:Barcode not configured");
        var pin = config["ForgeApi:Pin"] ?? throw new InvalidOperationException("ForgeApi:Pin not configured");

        var resp = await http.PostAsJsonAsync("/api/v1/auth/kiosk-login", new { barcode, pin }, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<KioskLoginResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("kiosk-login returned an empty body");

        _token = body.Token;
        logger.LogInformation("Adapter service authenticated to forge-api as {Email}", body.User.Email);
        return _token;
    }

    private async Task<HttpRequestMessage> AuthedRequestAsync(HttpMethod method, string path, CancellationToken ct)
    {
        if (_token is null)
            await LoginAsync(ct);

        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        return req;
    }

    public async Task<List<PartSummary>> GetPartsAsync(int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        using var req = await AuthedRequestAsync(HttpMethod.Get, $"/api/v1/parts?page={page}&pageSize={pageSize}", ct);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var page1 = await resp.Content.ReadFromJsonAsync<PartsPage>(JsonOpts, ct)
            ?? throw new InvalidOperationException("GET /parts returned an empty body");
        return page1.Items;
    }

    // Returns null when the part has no BOM revision yet (real state in this install —
    // see inventory.md gap note). Caller treats "no recipe" as legitimate, not an error.
    public async Task<BomRevisionDetail?> GetCurrentBomAsync(int partId, CancellationToken ct = default)
    {
        using var listReq = await AuthedRequestAsync(HttpMethod.Get, $"/api/v1/parts/{partId}/bom/revisions", ct);
        using var listResp = await http.SendAsync(listReq, ct);
        listResp.EnsureSuccessStatusCode();
        var revisions = await listResp.Content.ReadFromJsonAsync<List<BomRevisionSummary>>(JsonOpts, ct) ?? [];
        var current = revisions.FirstOrDefault(r => r.IsCurrent) ?? revisions.FirstOrDefault();
        if (current is null)
            return null;

        using var detailReq = await AuthedRequestAsync(HttpMethod.Get, $"/api/v1/parts/{partId}/bom/revisions/{current.Id}", ct);
        using var detailResp = await http.SendAsync(detailReq, ct);
        detailResp.EnsureSuccessStatusCode();
        return await detailResp.Content.ReadFromJsonAsync<BomRevisionDetail>(JsonOpts, ct);
    }

    // Checkpoint write path (B12: all persistence goes through forge-api HTTP, batched,
    // never per-tick). Positive delta -> receive-stock, negative -> use-stock. Verified
    // live against forge-api 2026-07-17 (see inventory.md B13).
    public async Task WriteCheckpointDeltaAsync(int partId, decimal delta, int? locationId, string reason, CancellationToken ct = default)
    {
        if (delta == 0)
            return;

        var path = delta > 0 ? "/api/v1/inventory/receive-stock" : "/api/v1/inventory/use-stock";
        var body = new
        {
            partId,
            locationId,
            quantity = Math.Abs(delta),
            reason,
            notes = "factory adapter sim checkpoint",
            lotNumber = (string?)null,
        };

        using var req = await AuthedRequestAsync(HttpMethod.Post, path, ct);
        req.Content = JsonContent.Create(body, options: JsonOpts);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        logger.LogInformation("Checkpoint delta written: part {PartId} {Delta:+0.####;-0.####} via {Path}", partId, delta, path);
    }

    public sealed record KioskLoginResponse(string Token, AuthUser User);
    public sealed record AuthUser(int Id, string Email);
    public sealed record PartsPage(List<PartSummary> Items, int TotalCount, int Page, int PageSize);
    public sealed record PartSummary(int Id, string PartNumber, string Name, string? Status, string ProcurementSource, string InventoryClass, int BomLineCount);
    public sealed record BomRevisionSummary(int Id, int PartId, int RevisionNumber, bool IsCurrent);
    public sealed record BomRevisionDetail(int Id, int PartId, int RevisionNumber, bool IsCurrent, List<BomRevisionLine> Entries);
    public sealed record BomRevisionLine(int Id, int PartId, string PartNumber, decimal Quantity, string SourceType);
}
