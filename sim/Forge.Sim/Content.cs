namespace Forge.Sim;

public sealed record ItemDef(int Id, string Name, int StackSize, bool Raw);

public sealed record Stack(int Item, int Count);

public sealed record RecipeDef(
    int Id, string Name, string Category, int Duration,
    IReadOnlyList<Stack> Inputs, IReadOnlyList<Stack> Outputs, IReadOnlyList<string> Flags)
{
    /// <summary>§3.1: GOAL = duration &lt;&lt; 16, in Q16.16 work units.</summary>
    public int Goal => Duration << Fx32.Shift;
}

public sealed record MachineDef(
    int Id, string Name, string Category, int SpeedBase,
    int PowerDraw, int PowerIdle, int InSlots, int OutSlots);

public sealed record TechDef(int Id, string Name, IReadOnlyList<int> Unlocks);

public sealed record ReferenceBuild(int TargetItem, int Miners, int Furnaces, int Assemblers);

public sealed class ContentValidationException(string message) : Exception(message);

/// <summary>
/// Loads and validates recipes-v0.toml. Per D14 the C# sim core owns content loading and parses
/// the authored TOML directly -- there is no JSON build step.
///
/// The parsed model is read explicitly, key by key, rather than reflection-mapped onto these
/// records. Convention binding would make a renamed or misspelled TOML key arrive as a default 0
/// instead of failing -- and a zero speed_base is a machine that never crafts, i.e. a content typo
/// becoming a silent sim bug. Explicit reads make a missing key a load error, which §2.4 requires.
/// This is also why MiniToml is used instead of a NuGet parser -- see MiniToml's class comment.
/// </summary>
public sealed class Content
{
    /// <summary>
    /// Ticks per second, from [meta] tick_hz. Read from content rather than hardcoded: it is a
    /// [CAL] value, and every rate in the spec (§3.1's R = 60σ/d) is derived from it. A sim whose
    /// tick rate disagrees with the content that calibrated it produces silently wrong throughput.
    /// </summary>
    public required int TickHz { get; init; }

    public required IReadOnlyList<ItemDef> Items { get; init; }
    public required IReadOnlyList<RecipeDef> Recipes { get; init; }
    public required IReadOnlyList<MachineDef> Machines { get; init; }
    public required IReadOnlyList<TechDef> Techs { get; init; }
    public required ReferenceBuild Build { get; init; }

    public RecipeDef Recipe(string name) =>
        Recipes.FirstOrDefault(r => r.Name == name)
        ?? throw new ContentValidationException($"no recipe named '{name}'");

    public MachineDef Machine(string name) =>
        Machines.FirstOrDefault(m => m.Name == name)
        ?? throw new ContentValidationException($"no machine named '{name}'");

    public static Content Load(string tomlPath)
    {
        var text = File.ReadAllText(tomlPath);
        var model = MiniToml.Parse(text, Path.GetFileName(tomlPath));
        var c = Parse(model);
        Validate(c);
        return c;
    }

    // ---- explicit TOML reads -------------------------------------------------------------

    private static Dictionary<string, object> Table(Dictionary<string, object> t, string key) =>
        t.TryGetValue(key, out var v) && v is Dictionary<string, object> tt
            ? tt
            : throw new ContentValidationException($"missing or non-table key '{key}'");

    private static List<Dictionary<string, object>> Array(Dictionary<string, object> t, string key) =>
        t.TryGetValue(key, out var v) && v is List<object> ta
            ? ta.Select((e, i) => e as Dictionary<string, object>
                ?? throw new ContentValidationException($"'{key}[{i}]' is not a table")).ToList()
            : throw new ContentValidationException($"missing or non-array key '{key}'");

    private static int Int(Dictionary<string, object> t, string key) =>
        t.TryGetValue(key, out var v) && v is long l
            ? checked((int)l)
            : throw new ContentValidationException($"missing or non-integer key '{key}'");

    private static string Str(Dictionary<string, object> t, string key) =>
        t.TryGetValue(key, out var v) && v is string s
            ? s
            : throw new ContentValidationException($"missing or non-string key '{key}'");

    private static bool Bool(Dictionary<string, object> t, string key, bool fallback = false) =>
        t.TryGetValue(key, out var v) && v is bool b ? b : fallback;

    private static IReadOnlyList<Stack> Stacks(Dictionary<string, object> t, string key)
    {
        if (!t.TryGetValue(key, out var v) || v is not List<object> arr)
            throw new ContentValidationException($"missing or non-array key '{key}'");
        var outp = new List<Stack>();
        foreach (var e in arr)
        {
            if (e is not Dictionary<string, object> st)
                throw new ContentValidationException($"'{key}' contains a non-table entry");
            outp.Add(new Stack(Int(st, "item"), Int(st, "count")));
        }
        return outp;
    }

    private static IReadOnlyList<string> Strings(Dictionary<string, object> t, string key)
    {
        if (!t.TryGetValue(key, out var v) || v is not List<object> arr) return [];
        return arr.OfType<string>().ToList();
    }

    private static IReadOnlyList<int> Ints(Dictionary<string, object> t, string key)
    {
        if (!t.TryGetValue(key, out var v) || v is not List<object> arr) return [];
        return arr.OfType<long>().Select(x => checked((int)x)).ToList();
    }

    private static Content Parse(Dictionary<string, object> m)
    {
        var items = Array(m, "items").Select(t =>
            new ItemDef(Int(t, "id"), Str(t, "name"), Int(t, "stack_size"), Bool(t, "raw"))).ToList();

        var recipes = Array(m, "recipes").Select(t =>
            new RecipeDef(Int(t, "id"), Str(t, "name"), Str(t, "category"), Int(t, "duration"),
                Stacks(t, "inputs"), Stacks(t, "outputs"), Strings(t, "flags"))).ToList();

        var machines = Array(m, "machines").Select(t =>
            new MachineDef(Int(t, "id"), Str(t, "name"), Str(t, "category"), Int(t, "speed_base"),
                Int(t, "power_draw"), Int(t, "power_idle"), Int(t, "in_slots"), Int(t, "out_slots"))).ToList();

        var techs = Array(m, "techs").Select(t =>
            new TechDef(Int(t, "id"), Str(t, "name"), Ints(t, "unlocks"))).ToList();

        var rb = Table(m, "reference_build");
        var build = new ReferenceBuild(Int(rb, "target_item"), Int(rb, "miners"),
            Int(rb, "furnaces"), Int(rb, "assemblers"));

        var tickHz = Int(Table(m, "meta"), "tick_hz");

        return new Content
        {
            TickHz = tickHz,
            Items = items, Recipes = recipes, Machines = machines, Techs = techs, Build = build
        };
    }

    // ---- §2.4 load-time validation -------------------------------------------------------

    /// <summary>
    /// Spec §2.4. Collects every failure before throwing: a content author fixing six typos one
    /// error-per-run at a time is the failure mode the spec's "error message quality IS the API"
    /// line is aimed at.
    /// </summary>
    public static void Validate(Content c)
    {
        var errs = new List<string>();
        var itemIds = c.Items.Select(i => i.Id).ToHashSet();

        // Not a numbered §2.4 rule, but the same class of failure: a zero or negative tick rate
        // makes every derived rate meaningless (division by zero in §3.1's R = tick_hz*σ/d).
        if (c.TickHz < 1)
            errs.Add($"meta.tick_hz is {c.TickHz}, must be >= 1");

        // R1: live item ids.
        foreach (var r in c.Recipes)
            foreach (var s in r.Inputs.Concat(r.Outputs))
                if (!itemIds.Contains(s.Item))
                    errs.Add($"R1 recipe '{r.Name}' references unknown item id {s.Item}");

        // R2: duration >= 1.
        foreach (var r in c.Recipes)
            if (r.Duration < 1)
                errs.Add($"R2 recipe '{r.Name}' has duration {r.Duration}, must be >= 1");

        // R3: every non-raw item reachable from Raw.
        var reach = c.Items.Where(i => i.Raw).Select(i => i.Id).ToHashSet();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var r in c.Recipes)
                if (r.Inputs.All(s => reach.Contains(s.Item)))
                    foreach (var s in r.Outputs)
                        changed |= reach.Add(s.Item);
        }
        foreach (var i in c.Items)
            if (!reach.Contains(i.Id))
                errs.Add($"R3 item '{i.Name}' is unreachable from Raw (orphan intermediate)");

        // R4: cycles. v0 content is a DAG, so the spectral-radius test is UNTESTED by real
        // content (`cycle-validator-untested`). Detect cycles and REJECT rather than pretend to
        // validate them -- rejecting is the safe direction until the real test exists.
        var producers = new Dictionary<int, List<RecipeDef>>();
        foreach (var r in c.Recipes)
            foreach (var s in r.Outputs)
                (producers.TryGetValue(s.Item, out var l) ? l : producers[s.Item] = []).Add(r);

        const int White = 0, Grey = 1, Black = 2;
        var color = itemIds.ToDictionary(i => i, _ => White);

        void Visit(int item, List<string> stack)
        {
            // Unknown ids are already reported by R1; skipping them keeps the actionable error
            // visible instead of dying here and burying it.
            if (!color.ContainsKey(item)) return;
            if (color[item] == Grey)
            {
                errs.Add($"R4 recipe cycle through item id {item} ({string.Join(" -> ", stack)}) -- " +
                         "the spectral-radius test is NOT implemented; cyclic content is rejected.");
                return;
            }
            if (color[item] == Black) return;
            color[item] = Grey;
            if (producers.TryGetValue(item, out var rs))
                foreach (var r in rs)
                    foreach (var s in r.Inputs)
                        Visit(s.Item, [.. stack, r.Name]);
            color[item] = Black;
        }
        foreach (var i in itemIds.OrderBy(x => x)) Visit(i, []);

        // R5: stack sizes non-zero.
        foreach (var i in c.Items)
            if (i.StackSize < 1)
                errs.Add($"R5 item '{i.Name}' has stack_size {i.StackSize}, must be >= 1");

        // R6: every recipe unlockable from the start tech set.
        var unlocked = c.Techs.SelectMany(t => t.Unlocks).ToHashSet();
        foreach (var r in c.Recipes)
            if (!unlocked.Contains(r.Id))
                errs.Add($"R6 recipe '{r.Name}' is not unlocked by any tech (dead content)");

        if (errs.Count > 0)
            throw new ContentValidationException(
                "content failed spec §2.4 validation:\n" + string.Join("\n", errs.Select(e => "  - " + e)));
    }
}
