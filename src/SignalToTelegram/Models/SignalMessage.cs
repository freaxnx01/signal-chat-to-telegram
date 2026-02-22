namespace SignalToTelegram.Models;

public record SignalMessage(
    long DateSentMs,
    string? Body,
    IReadOnlyList<SignalAttachment> Attachments
);
