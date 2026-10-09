using System.Text.Json;

namespace APIBack.Service;

public static class CommunicationEmojiRules
{
    // Mesma coleção Unicode do seletor mobile, incluindo tons de pele e famílias.
    // Lista explícita: texto livre não vira reação, mesmo se contiver um emoji.
    private static readonly HashSet<string> Allowed = Load();
    private static HashSet<string> Load()
    {
        using var stream = typeof(CommunicationEmojiRules).Assembly.GetManifestResourceStream("APIBack.communication-emojis.json")!;
        var set = JsonSerializer.Deserialize<HashSet<string>>(stream)!;
        set.UnionWith(new[] { "like", "heart", "thanks", "alert" });
        return set;
    }
    public static void Validate(string? reaction)
    {
        if (reaction != null && !Allowed.Contains(reaction)) throw new DeliveryDomainException(422, "CHAT_REACTION_INVALID", "Escolha um emoji para reagir.");
    }
}
