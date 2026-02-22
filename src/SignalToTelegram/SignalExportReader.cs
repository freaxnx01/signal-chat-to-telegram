using System.Text.Json;
using SignalToTelegram.Models;

namespace SignalToTelegram;

public class SignalExportReader
{
    private readonly string _exportPath;

    public SignalExportReader(string exportPath)
    {
        _exportPath = exportPath;
    }

    public IEnumerable<SignalMessage> ReadMessages(long afterDateSentMs = 0)
    {
        var jsonlPath = Path.Combine(_exportPath, "main.jsonl");
        var lines = File.ReadAllLines(jsonlPath);

        // Pass 1: find self recipient ID and chat ID
        string? selfRecipientId = null;
        string? selfChatId = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("recipient", out var recipient))
            {
                if (recipient.TryGetProperty("self", out _))
                    selfRecipientId = recipient.GetProperty("id").GetString();
            }

            if (selfRecipientId != null && selfChatId == null
                && root.TryGetProperty("chat", out var chat))
            {
                if (chat.TryGetProperty("recipientId", out var recId)
                    && recId.GetString() == selfRecipientId)
                    selfChatId = chat.GetProperty("id").GetString();
            }
        }

        if (selfChatId == null)
            throw new InvalidOperationException("Could not find 'Note to Self' chat in export.");

        // Pass 2: yield messages for the self chat
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("chatItem", out var chatItem)) continue;
            if (chatItem.GetProperty("chatId").GetString() != selfChatId) continue;
            if (!chatItem.TryGetProperty("standardMessage", out var standardMessage)) continue;

            var dateSent = long.Parse(chatItem.GetProperty("dateSent").GetString()!);
            if (dateSent <= afterDateSentMs) continue;

            string? body = null;
            if (standardMessage.TryGetProperty("text", out var text)
                && text.TryGetProperty("body", out var bodyEl))
                body = bodyEl.GetString();

            var attachments = new List<SignalAttachment>();
            if (standardMessage.TryGetProperty("attachments", out var attsEl))
            {
                foreach (var att in attsEl.EnumerateArray())
                {
                    if (!att.TryGetProperty("wasDownloaded", out var wd) || !wd.GetBoolean())
                    {
                        Console.WriteLine($"  [WARN] Attachment not downloaded, skipping.");
                        continue;
                    }

                    var pointer = att.GetProperty("pointer");
                    var contentType = pointer.GetProperty("contentType").GetString()!;
                    var locator = pointer.GetProperty("locatorInfo");
                    var plaintextHashB64 = locator.GetProperty("plaintextHash").GetString()!;

                    var hashBytes = Convert.FromBase64String(plaintextHashB64);
                    var hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
                    var ext = ContentTypeToExtension(contentType);
                    var filePath = Path.Combine(_exportPath, "files", hex[..2], hex + ext);

                    if (!File.Exists(filePath))
                    {
                        Console.WriteLine($"  [WARN] File not found: {filePath}, skipping.");
                        continue;
                    }

                    attachments.Add(new SignalAttachment(contentType, filePath));
                }
            }

            yield return new SignalMessage(dateSent, body, attachments);
        }
    }

    private static string ContentTypeToExtension(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png"  => ".png",
        "image/gif"  => ".gif",
        "image/webp" => ".webp",
        "video/mp4"  => ".mp4",
        "application/pdf" => ".pdf",
        _ => ".bin"
    };
}
