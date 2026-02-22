# Signal-to-Telegram Migration Tool — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a .NET 10 console app that reads a Signal Desktop JSONL backup and sends the "Note to Self" messages (text + attachments) to a Telegram chat via bot.

**Architecture:** Single project console app. No DI. Config from `appsettings.json`. Sequential pipeline: read → filter → send → save progress.

**Tech Stack:** .NET 10, Telegram.Bot NuGet, Microsoft.Extensions.Configuration.Json, System.Text.Json (built-in)

---

## Context: Signal JSONL Format

`main.jsonl` has one JSON object per line, mixed types:
- `{"version": ...}` — first line, skip
- `{"recipient": {"id": "21", "self": {...}}}` — self-recipient (no `contact` field, has `self` field)
- `{"recipient": {"id": "1", "contact": {...}}}` — other contacts
- `{"chat": {"id": "21", "recipientId": "21"}}` — chat linked to recipient
- `{"chatItem": {"chatId": "21", "dateSent": "1234567890000", "standardMessage": {"text": {"body": "hello"}, "attachments": [...]}}}` — message

Attachment file path: `plaintextHash` field in `pointer.locatorInfo` is base64-encoded SHA256. Convert to hex → file is at `files/{hex[0..1]}/{hex}.{ext}` relative to export directory.

Extension map: `image/jpeg` → `.jpg`, `image/png` → `.png`, `application/pdf` → `.pdf`, `video/mp4` → `.mp4`, otherwise `.bin`.

---

## Task 1: Scaffold the .NET project

**Files:**
- Create: `src/SignalToTelegram/SignalToTelegram.csproj`

**Step 1: Create project structure**

```bash
cd /home/freax/projects/github-repos/signal-chat-to-telegram
dotnet new console -n SignalToTelegram -o src/SignalToTelegram --framework net10.0
dotnet new sln -n signal-chat-to-telegram
dotnet sln add src/SignalToTelegram/SignalToTelegram.csproj
```

Expected: `src/SignalToTelegram/` created with `Program.cs` and `.csproj`.

**Step 2: Add NuGet packages**

```bash
cd src/SignalToTelegram
dotnet add package Telegram.Bot --version 22.6.1
dotnet add package Microsoft.Extensions.Configuration.Json --version 9.0.0
```

**Step 3: Verify build**

```bash
dotnet build src/SignalToTelegram
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

**Step 4: Commit**

```bash
git add src/ signal-chat-to-telegram.sln
git commit -m "chore: scaffold SignalToTelegram console app"
```

---

## Task 2: Configuration

**Files:**
- Create: `src/SignalToTelegram/appsettings.json`
- Modify: `src/SignalToTelegram/SignalToTelegram.csproj` (copy to output)

**Step 1: Create appsettings.json**

```json
{
  "Telegram": {
    "BotToken": "YOUR_BOT_TOKEN_HERE",
    "ChatId": "YOUR_CHAT_ID_HERE"
  },
  "Signal": {
    "ExportPath": "../../signal-export/signal-export-2026-02-22-17-36-37"
  },
  "RateLimitDelayMs": 500,
  "ProgressFile": "progress.json"
}
```

**Step 2: Make appsettings.json copy to output directory**

In `SignalToTelegram.csproj`, inside the existing `<Project>` block, add:

```xml
  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
```

**Step 3: Add appsettings.json to .gitignore** (it will contain the bot token)

Append to `.gitignore`:
```
appsettings.json
appsettings.*.json
*.user
```

**Step 4: Create appsettings.example.json** (safe to commit)

```json
{
  "Telegram": {
    "BotToken": "YOUR_BOT_TOKEN_HERE",
    "ChatId": "YOUR_CHAT_ID_HERE"
  },
  "Signal": {
    "ExportPath": "../../signal-export/signal-export-2026-02-22-17-36-37"
  },
  "RateLimitDelayMs": 500,
  "ProgressFile": "progress.json"
}
```

**Step 5: Commit**

```bash
git add src/SignalToTelegram/SignalToTelegram.csproj src/SignalToTelegram/appsettings.example.json .gitignore
git commit -m "chore: add configuration setup and appsettings.example.json"
```

---

## Task 3: Models

**Files:**
- Create: `src/SignalToTelegram/Models/SignalAttachment.cs`
- Create: `src/SignalToTelegram/Models/SignalMessage.cs`

**Step 1: Create `src/SignalToTelegram/Models/SignalAttachment.cs`**

```csharp
namespace SignalToTelegram.Models;

public record SignalAttachment(
    string ContentType,
    string LocalFilePath
);
```

**Step 2: Create `src/SignalToTelegram/Models/SignalMessage.cs`**

```csharp
namespace SignalToTelegram.Models;

public record SignalMessage(
    long DateSentMs,
    string? Body,
    IReadOnlyList<SignalAttachment> Attachments
);
```

**Step 3: Build to verify**

```bash
dotnet build src/SignalToTelegram
```

Expected: `Build succeeded.`

**Step 4: Commit**

```bash
git add src/SignalToTelegram/Models/
git commit -m "feat: add SignalMessage and SignalAttachment models"
```

---

## Task 4: SignalExportReader

**Files:**
- Create: `src/SignalToTelegram/SignalExportReader.cs`

**Step 1: Create `src/SignalToTelegram/SignalExportReader.cs`**

```csharp
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
```

**Step 2: Build to verify**

```bash
dotnet build src/SignalToTelegram
```

Expected: `Build succeeded.`

**Step 3: Commit**

```bash
git add src/SignalToTelegram/SignalExportReader.cs
git commit -m "feat: implement SignalExportReader for JSONL parsing"
```

---

## Task 5: ProgressTracker

**Files:**
- Create: `src/SignalToTelegram/ProgressTracker.cs`

**Step 1: Create `src/SignalToTelegram/ProgressTracker.cs`**

```csharp
using System.Text.Json;

namespace SignalToTelegram;

public class ProgressTracker
{
    private readonly string _filePath;

    public long LastSentDateMs { get; private set; }

    public ProgressTracker(string filePath)
    {
        _filePath = filePath;
        if (File.Exists(filePath))
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            LastSentDateMs = doc.RootElement.GetProperty("lastSentDateMs").GetInt64();
            Console.WriteLine($"Resuming from dateSent > {LastSentDateMs}");
        }
        else
        {
            LastSentDateMs = 0;
            Console.WriteLine("No progress file found, starting from the beginning.");
        }
    }

    public void Save(long dateSentMs)
    {
        LastSentDateMs = dateSentMs;
        File.WriteAllText(_filePath, JsonSerializer.Serialize(new { lastSentDateMs = dateSentMs }));
    }
}
```

**Step 2: Build to verify**

```bash
dotnet build src/SignalToTelegram
```

Expected: `Build succeeded.`

**Step 3: Commit**

```bash
git add src/SignalToTelegram/ProgressTracker.cs
git commit -m "feat: implement ProgressTracker for resume support"
```

---

## Task 6: TelegramSender

**Files:**
- Create: `src/SignalToTelegram/TelegramSender.cs`

**Step 1: Create `src/SignalToTelegram/TelegramSender.cs`**

```csharp
using Telegram.Bot;
using Telegram.Bot.Types;
using SignalToTelegram.Models;

namespace SignalToTelegram;

public class TelegramSender
{
    private readonly TelegramBotClient _bot;
    private readonly ChatId _chatId;

    public TelegramSender(string botToken, string chatId)
    {
        _bot = new TelegramBotClient(botToken);
        _chatId = new ChatId(chatId);
    }

    public async Task SendMessageAsync(SignalMessage message)
    {
        if (!message.Attachments.Any())
        {
            // Text only
            if (!string.IsNullOrEmpty(message.Body))
                await _bot.SendMessage(_chatId, message.Body);
            return;
        }

        // Has attachments: first attachment may get the caption
        bool captionUsed = false;
        foreach (var attachment in message.Attachments)
        {
            string? caption = null;
            if (!captionUsed && !string.IsNullOrEmpty(message.Body))
            {
                // If body > 1024 chars, send text separately first
                if (message.Body.Length > 1024)
                {
                    await _bot.SendMessage(_chatId, message.Body);
                }
                else
                {
                    caption = message.Body;
                }
                captionUsed = true;
            }

            await SendAttachmentAsync(attachment, caption);
        }
    }

    private async Task SendAttachmentAsync(SignalAttachment attachment, string? caption)
    {
        await using var stream = File.OpenRead(attachment.LocalFilePath);
        var file = InputFile.FromStream(stream, Path.GetFileName(attachment.LocalFilePath));

        if (attachment.ContentType.StartsWith("image/"))
        {
            await _bot.SendPhoto(_chatId, file, caption: caption);
        }
        else if (attachment.ContentType.StartsWith("video/"))
        {
            await _bot.SendVideo(_chatId, file, caption: caption);
        }
        else
        {
            await _bot.SendDocument(_chatId, file, caption: caption);
        }
    }
}
```

**Step 2: Build to verify**

```bash
dotnet build src/SignalToTelegram
```

Expected: `Build succeeded.`

**Step 3: Commit**

```bash
git add src/SignalToTelegram/TelegramSender.cs
git commit -m "feat: implement TelegramSender for text, photo, video, document"
```

---

## Task 7: Program.cs — main pipeline

**Files:**
- Modify: `src/SignalToTelegram/Program.cs`

**Step 1: Replace generated `Program.cs` with the pipeline**

```csharp
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
```

**Step 2: Build to verify**

```bash
dotnet build src/SignalToTelegram
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

**Step 3: Commit**

```bash
git add src/SignalToTelegram/Program.cs
git commit -m "feat: implement main pipeline in Program.cs"
```

---

## Task 8: Configure and run

**Step 1: Copy appsettings.example.json to appsettings.json**

```bash
cp src/SignalToTelegram/appsettings.example.json src/SignalToTelegram/appsettings.json
```

**Step 2: Fill in real values in `src/SignalToTelegram/appsettings.json`**

Edit the file manually:
- `Telegram.BotToken` — from @BotFather
- `Telegram.ChatId` — from @userinfobot (your user ID)

**Step 3: Verify the export path resolves correctly**

```bash
dotnet run --project src/SignalToTelegram -- --dry-run 2>/dev/null || dotnet run --project src/SignalToTelegram
```

If bot token is still placeholder, the app will fail at the Telegram API call but the reader will have logged the message count. Confirm you see something like:
```
Export path: /home/.../signal-export/signal-export-2026-02-22-17-36-37
Found 183 messages to send.
```

**Step 4: Run for real**

```bash
dotnet run --project src/SignalToTelegram
```

Watch the console output for `[1/183] Sending: ... OK`.

**Step 5: Add progress.json to .gitignore**

Append to `.gitignore`:
```
progress.json
```

**Step 6: Final commit**

```bash
git add .gitignore
git commit -m "chore: ignore progress.json"
```

---

## Notes on the ExportPath

The `ExportPath` in `appsettings.json` is resolved relative to the **output directory** (`bin/Debug/net10.0/`). The default value `../../signal-export/signal-export-2026-02-22-17-36-37` walks back to the repo root. If you run from a different working directory, use an absolute path instead.

Alternatively, add command-line override support in a future iteration.
