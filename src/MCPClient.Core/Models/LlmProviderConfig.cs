namespace MCPClient.Core.Models;

public enum LlmProviderType
{
    OpenAI,
    AzureOpenAI,
    Anthropic,
    Ollama,
    GitHubCopilot,
    ChatGPT
}

public class LlmProviderConfig
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public LlmProviderType Type { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? Endpoint { get; set; }  // For Azure OpenAI or Ollama
    public string? RefreshToken { get; set; }  // For OAuth providers (e.g. ChatGPT)
    public bool Enabled { get; set; } = true;
}
