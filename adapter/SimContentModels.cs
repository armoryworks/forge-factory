namespace Forge.Factory.Adapter;

// Shape mirrors data/recipes-v0.toml's items[]/recipes[] structure (D6: Part -> item,
// BomRevision -> recipe) so the sim-side JSON loader (D13's TOML->JSON build step) can
// read forge-sourced content with the same field names as the hand-authored v0 set.
// This is NOT a replacement for recipes-v0.toml — it is proof that live forge data can
// be transformed into the sim's content format on the cold path.

public sealed record SimItem(int Id, string Name, bool Raw);

public sealed record SimRecipeIo(int Item, decimal Count);

public sealed record SimRecipe(
    int Id,
    string Name,
    string? Category,
    List<SimRecipeIo> Inputs,
    List<SimRecipeIo> Outputs);

public sealed record SimContent(
    int Version,
    List<SimItem> Items,
    List<SimRecipe> Recipes,
    List<string> Gaps);
