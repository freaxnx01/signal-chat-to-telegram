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
