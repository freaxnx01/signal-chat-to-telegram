# Design: signal-chat-to-telegram

**Date:** 2026-02-22
**Stack:** C# / .NET 10 console app
**Goal:** Migrate Signal Desktop "Note to Self" export to a Telegram chat via bot API

---

## Requirements

- Read a Signal Desktop JSONL backup (`main.jsonl`)
- Filter to a single chat (default: "Note to Self", chatId matching self-recipient)
- Send messages to Telegram in chronological order
- Messages sent as clean content only (no timestamp/sender prefix)
- Attachments sent one-by-one (not as media groups)
- Track progress to avoid re-sending on re-run (`progress.json`)
- Configurable rate-limit delay between messages

---

## Input Format

Signal Desktop JSONL backup (`main.jsonl`):
- Mixed record types per line: `version`, `account`, `recipient`, `chat`, `chatItem`, etc.
- Self-recipient identified by `"self": {}` in the recipient record
- Messages are `chatItem` records with `standardMessage`, `updateMessage`, or `remoteDeletedMessage`
- Only `standardMessage` records are migrated (skip system events and deleted messages)
- `standardMessage` has optional `text.body` (string) and `attachments[]`
- Each attachment: `pointer.contentType`, `pointer.locatorInfo.plaintextHash` (base64 SHA256 → hex filename), `wasDownloaded` bool
- Local attachment files: `files/{hex[0..1]}/{hex}.{ext}` relative to export directory

---

## Architecture

Single .NET 10 console app, no DI framework.

```
src/SignalToTelegram/
├── Program.cs               # Entry: load config, run pipeline
├── appsettings.json         # BotToken, ChatId, ExportPath, RateLimitDelayMs, ChatId to export
├── Models/
│   ├── SignalMessage.cs     # Parsed chatItem: dateSent, body, attachments
│   └── SignalAttachment.cs  # contentType, local file path
├── SignalExportReader.cs    # Reads JSONL, resolves self chatId, yields SignalMessages
├── TelegramSender.cs        # Sends text / photo / document via Telegram.Bot
├── ProgressTracker.cs       # Reads/writes progress.json (last sent dateSent ms)
└── SignalToTelegram.csproj
```

---

## Data Flow

```
main.jsonl
    → SignalExportReader: parse lines, find self-recipient ID, filter chatItems for that chat
    → filter: only standardMessage, skip updateMessage / remoteDeletedMessage
    → filter: dateSent > lastSentMs (from ProgressTracker)
    → for each SignalMessage:
        → if text only: TelegramSender.SendTextAsync
        → if attachment(s): TelegramSender.SendAttachmentAsync per attachment
            → if text + first attachment: send photo/doc with caption
            → subsequent attachments: send without caption
        → ProgressTracker.Save(dateSent)
        → await delay (RateLimitDelayMs)
```

---

## Configuration (`appsettings.json`)

```json
{
  "Telegram": {
    "BotToken": "YOUR_BOT_TOKEN_HERE",
    "ChatId": "YOUR_CHAT_ID_HERE"
  },
  "Signal": {
    "ExportPath": "./signal-export/signal-export-2026-02-22-17-36-37",
    "ChatName": "Note to Self"
  },
  "RateLimitDelayMs": 500,
  "ProgressFile": "progress.json"
}
```

---

## Edge Cases

- **No text, only attachment(s):** send attachment(s) with no caption
- **Text only, no attachments:** send as plain text
- **Text + multiple attachments:** first attachment gets text as caption; rest sent bare
- **Long captions (> 1024 chars):** send text as separate message first, then attachment(s) without caption
- **Non-downloaded attachment** (`wasDownloaded: false`): log warning, skip attachment
- **Deleted messages** (`remoteDeletedMessage`): skip
- **System messages** (`updateMessage`): skip
- **PDF / non-image attachments:** use `SendDocumentAsync` instead of `SendPhotoAsync`
- **Already-sent messages:** progress.json stores last `dateSent` ms; messages with `dateSent <= lastSent` are skipped

---

## NuGet Dependencies

- `Telegram.Bot` — Telegram Bot API client
- `Microsoft.Extensions.Configuration.Json` — appsettings.json loading
- `System.Text.Json` — JSONL parsing (built-in)

---

## Out of Scope

- Multi-chat export
- Message timestamps in Telegram output
- Media groups / albums
- Reactions, quotes, replies
- Unit tests (minimal console app approach)
