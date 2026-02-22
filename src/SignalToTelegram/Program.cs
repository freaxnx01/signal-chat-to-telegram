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
var rateLimitDelayMs = int.TryParse(config["RateLimitDelayMs"], out var ms) ? ms : 500;
var progressFile = config["ProgressFile"] ?? "progress.json";

// Resolve relative paths from the app's base directory
if (!Path.IsPathRooted(exportPath))
    exportPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, exportPath));
if (!Path.IsPathRooted(progressFile))
    progressFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, progressFile));

Console.WriteLine($"Export path: {exportPath}");
Console.WriteLine($"Progress file: {progressFile}");
Console.WriteLine($"Rate limit: {rateLimitDelayMs}ms between messages");
Console.WriteLine();

var tracker = new ProgressTracker(progressFile);
var reader = new SignalExportReader(exportPath);
var sender = new TelegramSender(botToken, chatId);

var messages = reader.ReadMessages(tracker.LastSentDateMs)
    .OrderBy(m => m.DateSentMs)
    .ToList();
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
