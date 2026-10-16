using System.Text.Json;

namespace AtelierCapture.Archive;

internal static class RouteCountCatalog
{
    private static readonly Dictionary<string, string> Known = Read();
    internal static IEnumerable<KeyValuePair<string, string>> Routes => Known;

    internal static string? SystemId(string route) => Known.GetValueOrDefault(route);

    private static Dictionary<string, string> Read()
    {
        var assembly = typeof(RouteCountCatalog).Assembly;
        var name = assembly
            .GetManifestResourceNames()
            .SingleOrDefault(value => value.EndsWith(".Routes.json", StringComparison.Ordinal));
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/status"] = "Foundation",
        };
        if (name is null)
            return result;
        using var input = assembly.GetManifestResourceStream(name)!;
        using var document = JsonDocument.Parse(input);
        foreach (var route in document.RootElement.EnumerateObject())
        {
            var schemas = route.Value.EnumerateArray().Select(value => value.GetString()).ToArray();
            if (schemas.Length != 2 || schemas.Any(string.IsNullOrEmpty))
                throw new InvalidDataException("Capture route schema inventory is invalid.");
            var segment = route.Name.TrimStart('/').Split('/')[0];
            // Battle starts share one response contract across quest, exploration and gacha APIs.
            var owner =
                schemas[1] == "Blend.ApiSchema.Api.BattleStartResponse"
                    ? "Battle"
                    : segment switch
                    {
                        "battle" or "mod_timeline" => "Battle",
                        "atelier"
                        or "synthesis"
                        or "recipe"
                        or "tool"
                        or "ship"
                        or "illustrated_book"
                        or "communication" => "Atelier",
                        "auth" or "user" => "Accounts",
                        "character" or "memoria" or "emblem" => "Characters",
                        "party" or "rental_party" or "equipment_preset" => "Parties",
                        "mission" or "multi_mission" or "task" => "Missions",
                        "quest" or "solo_raid" or "total_battle" => "Quests",
                        "mail" => "Mail",
                        "gacha" => "Gacha",
                        "exploration" => "Dungeon",
                        "expedition" => "Expeditions",
                        "mana" or "stamina" => "Recovery",
                        "profile"
                        or "chara_home"
                        or "character_story"
                        or "login_bonus"
                        or "present" => "Home",
                        "shop"
                        or "purchase"
                        or "external_purchase"
                        or "daily_pass"
                        or "growth_pack"
                        or "special_offer"
                        or "refund_info" => "Shop",
                        "advertising_id" or "ktid" or "web_session" => "Foundation",
                        "invitation" => "Accounts",
                        "house_building" => "Housing",
                        "tutorial" => "Tutorial",
                        "item" => "Inventory",
                        _ => segment,
                    };
            if (!result.TryAdd(route.Name, owner))
                throw new InvalidDataException("Capture route inventory is duplicated.");
        }
        return result;
    }
}
