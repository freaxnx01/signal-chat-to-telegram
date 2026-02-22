using Telegram.Bot;
using Telegram.Bot.Types;
using SignalToTelegram.Models;

namespace SignalToTelegram;

public class TelegramSender
{
    private readonly TelegramBotClient _client;
    private readonly ChatId _chatId;

    public TelegramSender(string botToken, string chatId)
    {
        _client = new TelegramBotClient(botToken);
        _chatId = new ChatId(chatId);
    }

    public async Task SendMessageAsync(SignalMessage message)
    {
        if (!message.Attachments.Any())
        {
            if (!string.IsNullOrEmpty(message.Body))
            {
                await _client.SendMessage(_chatId, message.Body);
            }
            return;
        }

        bool captionUsed = false;
        foreach (var attachment in message.Attachments)
        {
            string? caption = null;

            if (!captionUsed && !string.IsNullOrEmpty(message.Body))
            {
                if (message.Body.Length > 1024)
                {
                    await _client.SendMessage(_chatId, message.Body);
                    captionUsed = true;
                    // caption stays null
                }
                else
                {
                    caption = message.Body;
                    captionUsed = true;
                }
            }

            await SendAttachmentAsync(attachment, caption);
        }
    }

    public async Task SendAttachmentAsync(SignalAttachment attachment, string? caption)
    {
        await using var stream = File.OpenRead(attachment.LocalFilePath);
        var fileName = Path.GetFileName(attachment.LocalFilePath);
        var inputFile = InputFile.FromStream(stream, fileName);

        if (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendPhoto(_chatId, inputFile, caption: caption);
        }
        else if (attachment.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendVideo(_chatId, inputFile, caption: caption);
        }
        else
        {
            await _client.SendDocument(_chatId, inputFile, caption: caption);
        }
    }
}
