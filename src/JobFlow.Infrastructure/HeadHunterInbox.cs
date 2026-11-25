using System.Text.Json;
using static JobFlow.Infrastructure.RemoteIntegrationService;

namespace JobFlow.Infrastructure;

internal static class HeadHunterInbox
{
    private const string Api = "https://api.hh.ru/common/chats";
    internal static async Task<List<IncomingFeedback>> ReadAsync(string site, Func<Uri, CancellationToken, Task<JsonDocument>> get, CancellationToken ct)
    {
        var host = site == "rabota.by" || site.EndsWith(".rabota.by", StringComparison.OrdinalIgnoreCase) ? "rabota.by" : "hh.ru";
        using var list = await get(new Uri(Api + $"?host={host}&per_page=20&page=0"), ct);
        var result = new List<IncomingFeedback>();
        foreach (var chat in list.RootElement.GetProperty("items").EnumerateArray().Take(20))
        {
            var chatId = Text(chat, "id");
            if (chatId.Length is < 1 or > 80 || chatId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) continue;
            using var detail = await get(new Uri(Api + $"/{chatId}/messages?host={host}&order=prev&limit=30"), ct);
            var data = detail.RootElement;
            var vacancyId = Text(data, "vacancy_id");
            if (vacancyId.Length is < 1 or > 20 || vacancyId.Any(c => !char.IsAsciiDigit(c))) vacancyId = "-";
            var subject = Text(Child(data, "display"), "title");
            if (string.IsNullOrWhiteSpace(subject)) subject = "Сообщение работодателя";
            foreach (var message in data.GetProperty("messages").EnumerateArray().Take(30))
            {
                var sender = Child(message, "sender_display_info");
                if (Text(sender, "role") != "EMPLOYER" || Child(sender, "is_current_participant").ValueKind != JsonValueKind.False) continue;
                var messageId = Text(message, "id");
                var body = Text(Child(message, "payload"), "text");
                if (messageId.Length is < 1 or > 100 || messageId.Contains(':') || string.IsNullOrWhiteSpace(body) ||
                    !DateTimeOffset.TryParse(Text(message, "creation_time"), out var time)) continue;
                result.Add(new($"hh-chat:{chatId}:{messageId}:{vacancyId}", subject[..Math.Min(2000, subject.Length)],
                    body[..Math.Min(100000, body.Length)], time.UtcDateTime));
            }
        }
        return result;
    }

    private static JsonElement Child(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;
    private static string Text(JsonElement value, string key) => Child(value, key).ToString();
}
