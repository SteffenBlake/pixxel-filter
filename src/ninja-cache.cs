#!/usr/bin/env dotnet
#:property TargetFramework=net11.0

// Port of SteffenBlake/pixxel-ninja-parser (MIT).
// API reference: https://poe.ninja/docs/api (checked 2026-11-22).
// Run: dotnet run --file src/ninja-cache.cs -- [--game poe1|poe2|all] [--league "Standard"]
// Paths default to this source file's directory. No packages or project file required.
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

try
{
    return await RunAsync(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
    if (args.Contains("--help"))
    {
        Console.WriteLine("""
            dotnet run --file src/ninja-cache.cs -- [options]
              --game poe1|poe2|all   Default: poe1. PoE2 is skipped for now.
              --league NAME         Default: first active PoE1 economy league.
              --output-dir PATH     Default: .ninja-cache beside this source file.
              --cache-dir PATH      Default: .responses inside the output directory.
              --self-test           Run offline checks; make no API requests.

            Output: poe1.json.
            poe2.json is reserved for the future PoE2 implementation.
            Tier thresholds below are inclusive lower bounds, expressed in Chaos Orbs.
            Raw responses are cached for six hours, then conditionally revalidated.
            Existing output is replaced only after every category has succeeded.
            """);
        return 0;
    }
    if (args.Length == 1 && args[0] == "--self-test")
    {
        Compiler.SelfTest();
        return 0;
    }

    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < args.Length; i += 2)
    {
        if (args[i] is not ("--game" or "--league" or "--output-dir" or "--cache-dir") ||
            i + 1 == args.Length || string.IsNullOrWhiteSpace(args[i + 1]) ||
            args[i + 1].StartsWith("--", StringComparison.Ordinal) ||
            !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException($"Invalid or duplicate option: {args[i]}. Use --help.");
    }
    string game = options.GetValueOrDefault("--game", "poe1");
    if (game is not ("poe1" or "poe2" or "all"))
        throw new ArgumentException("--game must be poe1, poe2, or all.");

    string outputDir = Path.GetFullPath(options.GetValueOrDefault("--output-dir",
        Path.Combine(Path.GetDirectoryName(SourcePath())!, ".ninja-cache")));
    if (game is "poe2" or "all")
        Console.WriteLine($"PoE2: skipped (not implemented); reserved output: {Path.Combine(outputDir, "poe2.json")}");
    if (game == "poe2") return 0;

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd(
        "PixxelFilter-NinjaCache/1.0 (+https://github.com/SteffenBlake/pixxel-filter)");
    http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    var api = new NinjaApi(http, Path.GetFullPath(options.GetValueOrDefault("--cache-dir",
        Path.Combine(outputDir, ".responses"))), cancellation.Token);
    const string baseUrl = "https://poe.ninja/poe1/api/economy";
    var leagues = await api.GetAsync(baseUrl + "/leagues") as JsonArray
        ?? throw new InvalidDataException("Expected an economy league array.");
    string league = options.GetValueOrDefault("--league") ?? leagues.FirstOrDefault()?["id"]?.GetValue<string>()
        ?? throw new InvalidDataException("No active PoE1 economy league found.");
    if (!leagues.Any(x => x?["id"]?.GetValue<string>() == league))
        throw new ArgumentException($"Unknown PoE1 economy league '{league}'. Available: " +
            string.Join(", ", leagues.Select(x => x?["id"]?.GetValue<string>())));
    Console.WriteLine($"PoE1 league: {league}");

    var compiler = new Compiler();
    // Currency-exchange data supersedes the old itemoverview/currencyoverview
    // requests for these categories. Category names are case-sensitive.
    string[] exchangeTypes = [
        "Currency", "Fragment", "Runegraft", "AllflameEmber", "Tattoo", "Omen",
        "DjinnCoin", "Ducat", "EnshroudingCrystal", "DivinationCard", "Artifact",
        "Oil", "DeliriumOrb", "Scarab", "Astrolabe", "Thread", "Fossil", "Resonator", "Essence"
    ];
    // Original stash strategies plus newer unique families. Categories such as
    // ImbuedGem, Viscera and IncursionTemple price specific modifiers/combinations;
    // they need dedicated filter matching before they can produce useful tiers.
    // HelmetEnchant and alternate-quality gem handlers are retired.
    string[] stashTypes = [
        "Incubator", "Vial", "Invitation", "SkillGem", "BaseType", "ClusterJewel",
        "UniqueWeapon", "UniqueArmour", "UniqueAccessory", "UniqueFlask", "UniqueJewel",
        "ForbiddenJewel", "ShrineBelt", "UniqueTincture", "UniqueRelic", "UniqueMap"
    ];
    foreach (string type in exchangeTypes)
    {
        var data = await api.GetAsync($"{baseUrl}/exchange/current/overview?league={Uri.EscapeDataString(league)}&type={type}");
        compiler.Exchange(type, data);
    }
    foreach (string type in stashTypes)
    {
        var data = await api.GetAsync($"{baseUrl}/stash/current/item/overview?league={Uri.EscapeDataString(league)}&type={type}");
        compiler.Stash(type, data);
    }
    var output = compiler.Finish();
    if (output.Count == 0) throw new InvalidDataException("No price groups produced; keeping existing output.");
    string outputPath = Path.Combine(outputDir, "poe1.json");
    await NinjaApi.WriteAtomicAsync(outputPath, output, cancellation.Token);
    Console.WriteLine($"Wrote {output.Count} groups to {outputPath}");
    return 0;
}

static string SourcePath([CallerFilePath] string path = "") => path;

sealed class NinjaApi(HttpClient http, string cacheDir, CancellationToken token)
{
    DateTimeOffset lastRequest = DateTimeOffset.MinValue;

    public async Task<JsonNode> GetAsync(string url)
    {
        // Hash the complete URL: game, league, endpoint family and type cannot collide.
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        string path = Path.Combine(cacheDir, key + ".json");
        JsonObject? cache = null;
        if (File.Exists(path))
        {
            try
            {
                cache = JsonNode.Parse(await File.ReadAllTextAsync(path, token)) as JsonObject;
                if (cache?["url"]?.GetValue<string>() != url || cache["data"] == null) cache = null;
            }
            catch (JsonException) { Console.Error.WriteLine($"Ignoring invalid cache: {path}"); }
        }
        if (cache?["checkedAt"] is JsonValue timestamp &&
            DateTimeOffset.TryParse(timestamp.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var checkedAt) &&
            checkedAt <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromHours(6))
        {
            Console.WriteLine($"Cached: {url}");
            return cache["data"]!;
        }

        for (int attempt = 0; ; attempt++)
        {
            var wait = TimeSpan.FromSeconds(3) - (DateTimeOffset.UtcNow - lastRequest);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cache?["etag"]?.GetValue<string>() is string etag)
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            lastRequest = DateTimeOffset.UtcNow;
            Console.WriteLine($"GET: {url}");
            using var response = await http.SendAsync(request, token);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 2)
            {
                var delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(10 * (attempt + 1));
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                continue;
            }
            JsonNode data;
            if (response.StatusCode == HttpStatusCode.NotModified && cache != null)
                data = cache["data"]!.DeepClone();
            else
            {
                response.EnsureSuccessStatusCode();
                data = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))
                    ?? throw new InvalidDataException($"Empty JSON response: {url}");
                if (data is not (JsonArray or JsonObject))
                    throw new InvalidDataException($"Unexpected JSON response: {url}");
            }
            var record = new JsonObject
            {
                ["url"] = url,
                ["checkedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["etag"] = response.Headers.ETag?.ToString() ?? cache?["etag"]?.GetValue<string>(),
                ["data"] = data.DeepClone()
            };
            if (response.Headers.CacheControl?.NoStore != true)
                await WriteAtomicAsync(path, record, token);
            return data;
        }
    }

    public static Task WriteAtomicAsync(string path, JsonNode data, CancellationToken token) =>
        WriteTextAtomicAsync(path, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", token);

    public static async Task WriteTextAtomicAsync(string path, string text, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

sealed class Compiler
{
    // Inclusive Chaos thresholds. Names are labels, not floating currency conversion rates.
    static readonly (string Name, decimal Minimum)[] Tiers = [
        ("MehTier", 0m), ("CommonTier", 0.1m), ("UsefulTier", 1m),
        ("ValuableTier", 5m), ("PremiumTier", 15m), ("ExceptionalTier", 50m),
        ("DivineTier", 100m), ("MirrorTier", 1000m)
    ];
    readonly SortedDictionary<string, SortedSet<string>> groups = new(StringComparer.Ordinal);
    readonly Dictionary<string, (decimal Min, decimal Max)> uniques = new(StringComparer.Ordinal);
    readonly Dictionary<(string Name, string Influence, int Level), decimal> bases = new();
    readonly Dictionary<(string Category, string Name), decimal> prices = new();

    void Price(string category, string name, decimal price)
    {
        if (name is "Scroll of Wisdom" or "Portal Scroll") return;
        var key = (category, name);
        prices[key] = prices.TryGetValue(key, out decimal previous) ? Math.Min(previous, price) : price;
    }

    static string Tier(decimal price) => Tiers.Last(x => price >= x.Minimum).Name;
    static string Text(JsonNode? node, string key) => node?[key]?.GetValue<string>() ?? "";
    static decimal Number(JsonNode? node, string key, decimal fallback = 0) => node?[key]?.GetValue<decimal>() ?? fallback;
    static bool Flag(JsonNode node, string key) => node[key]?.GetValue<bool>() ?? false;

    void Add(string group, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException($"Missing item name for {group}.");
        if (!groups.TryGetValue(group, out var names)) groups[group] = names = new(StringComparer.Ordinal);
        names.Add(name);
    }

    static JsonArray Lines(string category, JsonNode data)
    {
        var lines = data["lines"] as JsonArray ?? throw new InvalidDataException($"{category}: missing lines array.");
        if (lines.Count == 0) Console.Error.WriteLine($"{category}: no current prices; omitting groups.");
        return lines;
    }

    public void Exchange(string category, JsonNode data)
    {
        var lines = Lines(category, data);
        var core = data["core"] ?? throw new InvalidDataException($"{category}: missing exchange core.");
        string primary = Text(core, "primary");
        if (primary.Length == 0) throw new InvalidDataException($"{category}: missing primary currency.");
        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in (data["items"] as JsonArray ?? []).Concat(core["items"] as JsonArray ?? []))
            items[Text(item, "id")] = Text(item, "name");
        // rates are units of currency per primary unit, not primary per currency.
        // E.g. primary=divine and rates.chaos=200 means primaryValue * 200 Chaos.
        decimal chaosPerPrimary = primary == "chaos" ? 1m : Number(core["rates"], "chaos", -1m);
        if (chaosPerPrimary <= 0) throw new InvalidDataException($"{category}: cannot normalize {primary} to Chaos.");
        foreach (var line in lines)
        {
            string id = Text(line, "id");
            if (!items.TryGetValue(id, out var name)) throw new InvalidDataException($"{category}: unknown exchange id '{id}'.");
            decimal price = Number(line, "primaryValue", -1m);
            if (price < 0) throw new InvalidDataException($"{category}: invalid price for {name}.");
            Price(category, name, price * chaosPerPrimary);
        }
        if (category == "Currency")
        {
            Price("Currency", "Chaos Orb", 1m);
        }
    }

    public void Stash(string category, JsonNode data)
    {
        bool uniqueCategory = category.StartsWith("Unique", StringComparison.Ordinal) || category is "ForbiddenJewel" or "ShrineBelt";
        foreach (var node in Lines(category, data))
        {
            var line = node ?? throw new InvalidDataException($"{category}: null item line.");
            // Price ordinary unidentified drops, without linked, corrupted or foil premiums.
            if (uniqueCategory && (Number(line, "links") >= 5 || Flag(line, "corrupted") || Number(line, "itemClass", 3) != 3)) continue;
            decimal price = Number(line, "chaosValue", -1m);
            if (price < 0) throw new InvalidDataException($"{category}: missing or negative chaosValue.");
            string name = Text(line, "name");
            string tier = Tier(price);
            if (uniqueCategory)
            {
                string baseType = Text(line, "baseType");
                if (baseType.Length == 0) throw new InvalidDataException($"{category}: missing unique base type.");
                uniques[baseType] = uniques.TryGetValue(baseType, out var range)
                    ? (Math.Min(range.Min, price), Math.Max(range.Max, price)) : (price, price);
            }
            else if (category == "BaseType")
            {
                string variant = Text(line, "variant");
                if (Number(line, "listingCount") < 50 || variant.Contains('/')) continue;
                if (variant.Length == 0) variant = "Normal";
                if (variant is not ("Normal" or "Shaper" or "Elder" or "Crusader" or "Redeemer" or "Hunter" or "Warlord")) continue;
                // Minimum price across each minimum-item-level range avoids promoting
                // higher-level drops using an expensive, scarce lower-level listing.
                foreach (int level in new[] { 0, 83, 84, 85, 86 })
                {
                    if (Number(line, "levelRequired") < level) continue;
                    var key = (name, variant, level);
                    bases[key] = bases.TryGetValue(key, out decimal previous) ? Math.Min(previous, price) : price;
                }
            }
            else if (category == "ClusterJewel")
            {
                if (Number(line, "levelRequired") <= 1) continue;
                if (!int.TryParse(Text(line, "variant").Split(' ')[0], out int passives))
                    throw new InvalidDataException($"Unknown cluster variant: {Text(line, "variant")}");
                string size = Text(line, "baseType") switch
                {
                    "Large Cluster Jewel" => "Large", "Medium Cluster Jewel" => "Medium",
                    "Small Cluster Jewel" => "Small",
                    _ => throw new InvalidDataException($"Unknown cluster base: {Text(line, "baseType")}")
                };
                if (!ClusterNames.TryGetValue(name, out string? enchantment))
                    throw new InvalidDataException($"Unknown cluster enchantment: {name}");
                Add($"{tier}_{size}_P{passives}_i{Number(line, "levelRequired")}_ClusterJewel", enchantment);
            }
            else if (category == "SkillGem")
            {
                // Combined Vaal/transfigured display names cannot be matched as BaseType.
                // Omit these rather than pricing every Vaal gem by its hidden paired gem.
                if (name.Contains(" (", StringComparison.Ordinal)) continue;
                if (Number(line, "gemLevel") < 1)
                    throw new InvalidDataException($"{category}: missing gemLevel for {name}.");
                string state = Flag(line, "corrupted") ? "Corrupted" : "Uncorrupted";
                Add($"{tier}_L{Number(line, "gemLevel")}_Q{Number(line, "gemQuality")}_{state}_{category}", name);
            }
            else Price(category, name, price);
        }
    }

    public JsonObject Finish()
    {
        foreach (var (item, price) in prices)
        {
            Add($"{Tier(price)}_{item.Category}", item.Name);
        }
        foreach (var (name, range) in uniques)
            Add($"{Tier((range.Min + range.Max) / 2m)}_Unique", name);
        foreach (var (item, price) in bases)
        {
            string tier = price >= 50 ? "ExceptionalTier" : price >= 5 ? "ValuableTier" : "WorthlessTier";
            string level = item.Level == 0 ? "Any" : item.Level.ToString(CultureInfo.InvariantCulture);
            Add($"{tier}_{item.Influence}_{level}_Bases", item.Name);
        }
        var output = new JsonObject();
        foreach (var (key, names) in groups)
            output[key] = new JsonArray(names.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        return output;
    }

    // Cluster mappings are embedded so running this file requires no extra resources.
    // Based on pixxel-ninja-parser's xan_filter-derived lookup, updated against live data.
    static readonly Dictionary<string, string> ClusterNames = new(StringComparer.Ordinal)
    {
        ["+12% to Chaos Resistance"] = "Chaos Resistance",
        ["+15% to Cold Resistance"] = "Cold Resistance",
        ["+15% to Fire Resistance"] = "Fire Resistance",
        ["+15% to Lightning Resistance"] = "Lightning Resistance",
        ["+2% Chance to Block Attack Damage"] = "Chance to Block Attack Damage",
        ["+4% chance to Suppress Spell Damage"] = "Chance to Suppress Spell Damage",
        ["10% increased Area Damage"] = "Area Damage",
        ["10% increased Attack Damage"] = "Attack Damage",
        ["10% increased Damage over Time"] = "Damage over Time",
        ["10% increased Damage while affected by a Herald"] = "Damage while you have a Herald",
        ["10% increased Effect of Non-Damaging Ailments"] = "Effect of Non-Damaging Ailments",
        ["10% increased Elemental Damage"] = "Elemental Damage",
        ["10% increased Life Recovery from Flasks, 10% increased Mana Recovery from Flasks"] = "Life and Mana recovery from Flasks",
        ["10% increased Projectile Damage"] = "Projectile Damage",
        ["10% increased Spell Damage"] = "Spell Damage",
        ["12% increased Attack Damage while Dual Wielding"] = "Attack Damage while Dual Wielding",
        ["12% increased Attack Damage while holding a Shield"] = "Attack Damage while holding a Shield",
        ["12% increased Brand Damage"] = "Brand Damage",
        ["12% increased Burning Damage"] = "Fire Damage over Time",
        ["12% increased Chaos Damage"] = "Chaos Damage",
        ["12% increased Chaos Damage over Time"] = "Chaos Damage over Time",
        ["12% increased Cold Damage"] = "Cold Damage",
        ["12% increased Cold Damage over Time"] = "Cold Damage over Time",
        ["12% increased Damage with Bows, 12% increased Damage Over Time with Bow Skills"] = "Bow Damage",
        ["12% increased Damage with Two Handed Weapons"] = "Damage with Two Handed Melee Weapons",
        ["12% increased Fire Damage"] = "Fire Damage",
        ["12% increased Lightning Damage"] = "Lightning Damage",
        ["12% increased Physical Damage"] = "Physical Damage",
        ["12% increased Physical Damage over Time"] = "Physical Damage over Time",
        ["12% increased Totem Damage"] = "Totem Damage",
        ["12% increased Trap Damage, 12% increased Mine Damage"] = "Trap and Mine Damage",
        ["15% increased Armour"] = "Armour",
        ["15% increased Critical Strike Chance"] = "Critical Chance",
        ["15% increased Evasion Rating"] = "Evasion",
        ["2% Chance to Block Spell Damage"] = "Chance to Block Spell Damage",
        ["2% increased Effect of your Curses"] = "Curse Effect",
        ["4% increased maximum Life"] = "Life",
        ["6% increased Flask Effect Duration"] = "Flask Duration",
        ["6% increased Mana Reservation Efficiency of Skills"] = "Reservation Efficiency",
        ["6% increased maximum Energy Shield"] = "Energy Shield",
        ["6% increased maximum Mana"] = "Mana",
        ["Axe Attacks deal 12% increased Damage with Hits and Ailments, Sword Attacks deal 12% increased Damage with Hits and Ailments"] = "Axe and Sword Damage",
        ["Channelling Skills deal 12% increased Damage"] = "Channelling Skill Damage",
        ["Claw Attacks deal 12% increased Damage with Hits and Ailments, Dagger Attacks deal 12% increased Damage with Hits and Ailments"] = "Dagger and Claw Damage",
        ["Exerted Attacks deal 20% increased Damage"] = "Exerted Attack Damage",
        ["Minions deal 10% increased Damage"] = "Minion Damage",
        ["Minions deal 10% increased Damage while you are affected by a Herald"] = "Minion Damage while you have a Herald",
        ["Minions have 12% increased maximum Life"] = "Minion Life",
        ["Staff Attacks deal 12% increased Damage with Hits and Ailments, Mace or Sceptre Attacks deal 12% increased Damage with Hits and Ailments"] = "Mace and Staff Damage",
        ["Wand Attacks deal 12% increased Damage with Hits and Ailments"] = "Wand Damage",
    };

    public static void SelfTest()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Self-test failed: " + message);
            checks++;
        }
        Check(Tier(0) == "MehTier", "zero price starts at visual tier three");
        foreach (var tier in Tiers)
            Check(Tier(tier.Minimum) == tier.Name, "inclusive boundary " + tier.Name);
        Check(Tier(99.99m) == "ExceptionalTier", "below boundary");
        Check(Tier(208.2m) == "DivineTier", "one Divine Orb is divine tier");
        var compiler = new Compiler();
        compiler.Exchange("Currency", JsonNode.Parse("""
            {"core":{"primary":"divine","rates":{"chaos":200},"items":[]},
             "items":[{"id":"test","name":"Test Orb"}],"lines":[{"id":"test","primaryValue":2}]}
            """)!);
        compiler.Stash("UniqueWeapon", JsonNode.Parse("""
            {"lines":[{"name":"A","baseType":"Test Sword","chaosValue":1},
                      {"name":"B","baseType":"Test Sword","chaosValue":1001},
                      {"name":"Linked","baseType":"Test Sword","chaosValue":200000,"links":6},
                      {"name":"Linked","baseType":"Test Sword","chaosValue":100000,"links":5},
                      {"name":"Corrupted","baseType":"Test Sword","chaosValue":300000,"corrupted":true},
                      {"name":"Foil","baseType":"Test Sword","chaosValue":400000,"itemClass":9},
                      {"name":"Foil","baseType":"Test Sword","chaosValue":500000,"itemClass":10},
                      {"name":"Linked only","baseType":"Linked Only Base","chaosValue":100000,"links":6}]}
            """)!);
        compiler.Stash("UniqueArmour", JsonNode.Parse("""
            {"lines":[{"name":"C","baseType":"Test Sword","chaosValue":3},
                      {"name":"D","baseType":"Single Price Base","chaosValue":15},
                      {"name":"E","baseType":"Boundary Base","chaosValue":40},
                      {"name":"F","baseType":"Boundary Base","chaosValue":60},
                      {"name":"G","baseType":"Boundary Base","chaosValue":42}]}
            """)!);
        compiler.Stash("BaseType", JsonNode.Parse("""
            {"lines":[{"name":"Test Base","chaosValue":20,"listingCount":50,"levelRequired":84},
                      {"name":"Test Base","chaosValue":5,"listingCount":50,"levelRequired":85},
                      {"name":"Test Base","chaosValue":50,"listingCount":50,"levelRequired":87},
                      {"name":"Cheap Base","chaosValue":4,"listingCount":50,"levelRequired":82},
                      {"name":"Influenced Base","chaosValue":50,"listingCount":50,"levelRequired":83,"variant":"Hunter"},
                      {"name":"Double Base","chaosValue":100,"listingCount":100,"variant":"Shaper/Hunter"},
                      {"name":"Rare Listing","chaosValue":100,"listingCount":1}]}
            """)!);
        compiler.Stash("ClusterJewel", JsonNode.Parse("""
            {"lines":[{"name":"Minions deal 10% increased Damage","baseType":"Large Cluster Jewel",
             "variant":"12 passives","chaosValue":30,"levelRequired":84}]}
            """)!);
        compiler.Stash("SkillGem", JsonNode.Parse("""
            {"lines":[{"name":"Fireball of Test","chaosValue":30,"gemLevel":1,"gemQuality":20},
             {"name":"Fireball of Test","chaosValue":30,"gemLevel":1,"gemQuality":20},
             {"name":"Vaal Fireball (Fireball of Test)","chaosValue":1000,"gemLevel":21,"corrupted":true}]}
            """)!);
        var output = compiler.Finish();
        Check(output["DivineTier_Currency"]?[0]?.GetValue<string>() == "Test Orb", "exchange conversion and metadata join");
        Check(output["UsefulTier_Currency"]?[0]?.GetValue<string>() == "Chaos Orb", "synthetic Chaos");
        Check(!output.Any(x => (x.Value as JsonArray)?.Any(n => n?.GetValue<string>() == "Scroll of Wisdom") == true), "scrolls excluded from economy tiers");
        Check(compiler.uniques["Test Sword"] == (1m, 1001m), "unique min/max across categories ignore special prices");
        Check(output["DivineTier_Unique"]?[0]?.GetValue<string>() == "Test Sword", "unique range midpoint selects tier");
        Check(output["PremiumTier_Unique"]?[0]?.GetValue<string>() == "Single Price Base", "single unique price");
        Check(output["ExceptionalTier_Unique"]?[0]?.GetValue<string>() == "Boundary Base", "unique midpoint at inclusive threshold");
        Check(!output.ContainsKey("VariedTier_Unique"), "no varied unique tier");
        Check(!compiler.uniques.ContainsKey("Linked Only Base"), "special-only bases are excluded");
        Check(output["ValuableTier_Normal_Any_Bases"]?[0]?.GetValue<string>() == "Test Base", "unrestricted base bucket");
        Check(output["ValuableTier_Normal_84_Bases"]?[0]?.GetValue<string>() == "Test Base", "base range uses minimum price, inclusive five-chaos threshold");
        Check(output["ExceptionalTier_Normal_86_Bases"]?[0]?.GetValue<string>() == "Test Base", "86-plus bucket, inclusive fifty-chaos threshold");
        Check(output["WorthlessTier_Normal_Any_Bases"]?[0]?.GetValue<string>() == "Cheap Base", "cheap bases retained without item-level restriction");
        Check(output["ExceptionalTier_Hunter_83_Bases"]?[0]?.GetValue<string>() == "Influenced Base", "influence-specific base bucket");
        Check(!output.Any(x => x.Key.EndsWith("_Bases", StringComparison.Ordinal) && (x.Value as JsonArray)!.Any(n => n!.GetValue<string>() is "Double Base" or "Rare Listing")), "dual influence and sparse listings excluded");
        Check(output["PremiumTier_Large_P12_i84_ClusterJewel"]?[0]?.GetValue<string>() == "Minion Damage", "cluster translation");
        Check((output["PremiumTier_L1_Q20_Uncorrupted_SkillGem"] as JsonArray)?.Count == 1, "gem dimensions and deduplication");
        Check(!output.Any(x => x.Key.Contains("L21", StringComparison.Ordinal)), "ambiguous Vaal display names omitted");
        bool rejected = false;
        try { compiler.Exchange("Oil", JsonNode.Parse("""{"core":{"primary":"exalted","rates":{}},"lines":[]}""")!); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "missing conversion fails explicitly");
        Check(!output.Any(x => x.Key.Contains("_Stack", StringComparison.Ordinal)), "only unit-price tier variables");
        Console.WriteLine($"Passed {checks} offline checks.");
    }
}
