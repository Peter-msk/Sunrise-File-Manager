namespace Sunrise.FileManager.Services;

public interface IAiIntentAdapter
{
    Task<AiNaturalLanguageService.Intent> InterpretAsync(string request, CancellationToken token);
}

public sealed class AiOfflineIntentAdapter : IAiIntentAdapter
{
    public Task<AiNaturalLanguageService.Intent> InterpretAsync(string request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(AiNaturalLanguageService.Parse(request));
    }
}

public sealed class OllamaUserConfig
{
    public bool HasPromptedSetup { get; set; }
    public bool UserAcceptedInstall { get; set; }
    public bool OllamaEnabled { get; set; }
    public string Endpoint { get; set; } = AiOllamaService.DefaultEndpoint;
    public string Model { get; set; } = AiOllamaService.DefaultModel;
    public DateTimeOffset? LastUpdatedUtc { get; set; }
}

public sealed record OllamaStatusReport(
    bool IsInstalled,
    bool IsRunning,
    string? ExecutablePath,
    string Endpoint,
    string ActiveModel,
    IReadOnlyList<string> InstalledModels,
    string StatusMessage);
