using System.Text.Json;

namespace Forge.Factory.Adapter;

// Cold-path only: runs once at startup / on demand, never on the tick path.
// Fetches Part -> item and BomRevision -> recipe (D6 mapping) and writes a
// sim-consumable JSON file. The sim/scaffold reads this file directly — it
// never touches HTTP or Postgres itself (B3).
public sealed class ColdPathLoader(ForgeApiClient forge, IConfiguration config, ILogger<ColdPathLoader> logger)
{
    public async Task<SimContent> RunAsync(CancellationToken ct = default)
    {
        var partIds = config.GetSection("ColdPath:PartIds").Get<int[]>() ?? [];
        var gaps = new List<string>();

        // WorkCenter (machine) mapping intentionally skipped — endpoint shape was not
        // curl-verified in the survey pass and doing so now would cost time this slice
        // doesn't need. Logged by name per instruction; recipes below carry Category = null.
        gaps.Add("WorkCenter mapping skipped — recipes have no machine/category assignment (see inventory.md).");

        var allParts = await forge.GetPartsAsync(ct: ct);
        var wanted = partIds.Length > 0
            ? allParts.Where(p => partIds.Contains(p.Id)).ToList()
            : allParts;

        var items = wanted
            .Select(p => new SimItem(p.Id, p.Name, Raw: p.ProcurementSource == "Buy"))
            .ToList();

        var recipes = new List<SimRecipe>();
        foreach (var part in wanted)
        {
            var bom = await forge.GetCurrentBomAsync(part.Id, ct);
            if (bom is null || bom.Entries.Count == 0)
            {
                gaps.Add($"Part {part.PartNumber} (id={part.Id}) has no BOM revision — no recipe emitted.");
                continue;
            }

            recipes.Add(new SimRecipe(
                Id: bom.Id,
                Name: $"make-{part.PartNumber}",
                Category: null,
                Inputs: bom.Entries.Select(e => new SimRecipeIo(e.PartId, e.Quantity)).ToList(),
                Outputs: [new SimRecipeIo(part.Id, 1)]));
        }

        var content = new SimContent(Version: 0, items, recipes, gaps);

        var outputPath = ResolveOutputPath();
        var json = JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, json, ct);

        logger.LogInformation("Cold-path load wrote {Items} items, {Recipes} recipes, {Gaps} gaps to {Path}",
            items.Count, recipes.Count, gaps.Count, outputPath);

        return content;
    }

    // Resolved against the working directory the service is launched from (the adapter/
    // project dir, per this repo's `dotnet run` convention), not the build output dir —
    // so the default "../data/live-import.json" lands at factory/data/live-import.json,
    // a sibling the Godot scaffold can read without knowing about the adapter's bin layout.
    private string ResolveOutputPath()
    {
        var configured = config["ColdPath:OutputPath"] ?? "../data/live-import.json";
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configured));
    }
}
