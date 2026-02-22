using Microsoft.Extensions.Configuration;
using SignalToTelegram;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var botToken = config["Telegram:BotToken"]
    ?? throw new InvalidOperationException("Telegram:BotToken not configured.");
var chatId = config["Telegram:ChatId"]
    ?? throw new InvalidOperationException("Telegram:ChatId not configured.");
var exportPath = config["Signal:ExportPath"]
    ?? throw new InvalidOperationException("Signal:ExportPath not configured.");
var rateLimitDelayMs = int.Parse(config["RateLimitDelayMs"] ?? "500");
var progressFile = config["ProgressFile"] ?? "progress.json";

// Resolve relative export path from the app's base directory
if (!Path.IsPathRooted(exportPath))
    exportPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, exportPath));

Console.WriteLine($"Export path: {exportPath}");
Console.WriteLine($"Rate limit: {rateLimitDelayMs}ms between messages");
Console.WriteLine();

var tracker = new ProgressTracker(progressFile);
var reader = new SignalExportReader(exportPath);
var sender = new TelegramSender(botToken, chatId);

var messages = reader.ReadMessages(tracker.LastSentDateMs).ToList();
Console.WriteLine($"Found {messages.Count} messages to send.");
Console.WriteLine();

int sent = 0;
foreach (var message in messages)
{
    var preview = message.Body?.Length > 60
        ? message.Body[..60] + "..."
        : message.Body ?? $"[{message.Attachments.Count} attachment(s)]";
    Console.Write($"[{sent + 1}/{messages.Count}] Sending: {preview} ... ");

    await sender.SendMessageAsync(message);
    tracker.Save(message.DateSentMs);
    sent++;

    Console.WriteLine("OK");
    await Task.Delay(rateLimitDelayMs);
}

Console.WriteLine();
Console.WriteLine($"Done. Sent {sent} messages.");
