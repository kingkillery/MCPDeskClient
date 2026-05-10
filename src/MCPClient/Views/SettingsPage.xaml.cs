using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MCPClient.Core.LlmProviders;
using MCPClient.Core.Models;
using MCPClient.Core.Services;
using MCPClient.ViewModels;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace MCPClient.Views;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel _viewModel;
    private readonly IConfigurationService _configService;
    private readonly ILlmService _llmService;
    private CancellationTokenSource? _authCts;
    private bool _isLoaded;
    
    public SettingsPage()
    {
        this.InitializeComponent();
        
        _viewModel = App.Services.GetRequiredService<SettingsViewModel>();
        _configService = App.Services.GetRequiredService<IConfigurationService>();
        _llmService = App.Services.GetRequiredService<ILlmService>();
        
        this.Loaded += SettingsPage_Loaded;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadSettingsAsync();
        ProvidersListView.ItemsSource = _viewModel.Providers;
        ConfigPathTextBlock.Text = _configService.GetConfigFilePath();
        
        // Load GitHub Copilot state
        try
        {
            var config = await _configService.LoadConfigAsync();
            var ghProvider = config.LlmProviders.Values.FirstOrDefault(p => p.Type == LlmProviderType.GitHubCopilot);
            if (ghProvider != null)
            {
                GitHubPatBox.Password = ghProvider.ApiKey ?? string.Empty;
                
                // Set model combo box
                var modelText = ghProvider.Model;
                bool found = false;
                for (int i = 0; i < GitHubModelComboBox.Items.Count; i++)
                {
                    if (GitHubModelComboBox.Items[i] is ComboBoxItem item && item.Content?.ToString() == modelText)
                    {
                        GitHubModelComboBox.SelectedIndex = i;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    GitHubModelComboBox.SelectedIndex = 0;
                }
                
                UpdateGitHubStatus(!string.IsNullOrEmpty(ghProvider.ApiKey));
            }
            else
            {
                UpdateGitHubStatus(false);
            }
            
            // Load ChatGPT state
            var chatGptProviderCfg = config.LlmProviders.Values.FirstOrDefault(p => p.Type == LlmProviderType.ChatGPT);
            if (chatGptProviderCfg != null)
            {
                var modelText = chatGptProviderCfg.Model;
                bool found = false;
                for (int i = 0; i < ChatGptModelComboBox.Items.Count; i++)
                {
                    if (ChatGptModelComboBox.Items[i] is ComboBoxItem item && item.Content?.ToString() == modelText)
                    {
                        ChatGptModelComboBox.SelectedIndex = i;
                        found = true;
                        break;
                    }
                }
                if (!found) ChatGptModelComboBox.SelectedIndex = 0;

                UpdateChatGptStatus(!string.IsNullOrEmpty(chatGptProviderCfg.ApiKey));
            }
            else
            {
                UpdateChatGptStatus(false);
            }
        }
        catch (Exception)
        {
            UpdateGitHubStatus(false);
        }
        
        _isLoaded = true;
    }
    
    private void UpdateGitHubStatus(bool isAuthenticated)
    {
        if (isAuthenticated)
        {
            GitHubAuthStatus.Text = "✓ Connected";
            GitHubAuthStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
            GitHubSignInButton.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE73E", FontSize = 16 },
                    new TextBlock { Text = "Re-authenticate" }
                }
            };
        }
        else
        {
            GitHubAuthStatus.Text = "Not connected";
            GitHubAuthStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
        }
    }
    
    private void UpdateChatGptStatus(bool isAuthenticated)
    {
        if (isAuthenticated)
        {
            ChatGptAuthStatus.Text = "✓ Connected";
            ChatGptAuthStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
            ChatGptSignInButton.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE73E", FontSize = 16 },
                    new TextBlock { Text = "Re-authenticate" }
                }
            };
        }
        else
        {
            ChatGptAuthStatus.Text = "Not connected";
            ChatGptAuthStatus.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
        }
    }
    
    private async void ChatGptSignIn_Click(object sender, RoutedEventArgs e)
    {
        _authCts?.Cancel();
        _authCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var tempConfig = new LlmProviderConfig { Type = LlmProviderType.ChatGPT };
        var provider   = new MCPClient.Core.LlmProviders.ChatGptProvider();
        provider.Configure(tempConfig);

        ChatGptSignInButton.IsEnabled = false;
        ChatGptStatusText.Visibility  = Visibility.Collapsed;

        provider.AuthenticationCompleted += async () =>
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var selectedModel = (ChatGptModelComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                    ?? "gpt-4o";

                var config = await _configService.LoadConfigAsync();
                if (!config.LlmProviders.ContainsKey("chatgpt"))
                {
                    config.LlmProviders["chatgpt"] = new LlmProviderConfig
                    {
                        Id          = "chatgpt",
                        DisplayName = "ChatGPT",
                        Type        = LlmProviderType.ChatGPT,
                        Enabled     = true
                    };
                }

                config.LlmProviders["chatgpt"].ApiKey = tempConfig.ApiKey;
                config.LlmProviders["chatgpt"].Model  = selectedModel;
                config.DefaultProviderId = "chatgpt";

                await _configService.SaveConfigAsync(config);
                _llmService.ConfigureProviders(config);

                ChatGptStatusText.Text       = "✓ Signed in successfully!";
                ChatGptStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
                ChatGptStatusText.Visibility = Visibility.Visible;
                UpdateChatGptStatus(true);

                await _viewModel.LoadSettingsAsync();
                ChatGptSignInButton.IsEnabled = true;
            });
        };

        try
        {
            // Opens the user's default browser for OpenAI login, then captures
            // the loopback redirect containing the authorization code.
            await provider.AuthenticateWithBrowserAsync(_authCts.Token);
        }
        catch (OperationCanceledException)
        {
            ChatGptStatusText.Text       = "Sign-in timed out or was cancelled.";
            ChatGptStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
            ChatGptStatusText.Visibility = Visibility.Visible;
            ChatGptSignInButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ChatGptStatusText.Text       = $"Sign-in failed: {ex.Message}";
            ChatGptStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
            ChatGptStatusText.Visibility = Visibility.Visible;
            ChatGptSignInButton.IsEnabled = true;
        }
    }
    
    private void ChatGptModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Model selection is persisted on Save Settings
    }
    
    private async void GitHubSignIn_Click(object sender, RoutedEventArgs e)
    {
        _authCts?.Cancel();
        _authCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        
        var provider = new GitHubCopilotProvider();
        // Set up a temp config so the provider can write the token back to it
        var tempConfig = new LlmProviderConfig { Type = LlmProviderType.GitHubCopilot };
        provider.Configure(tempConfig);
        
        GitHubSignInButton.IsEnabled = false;
        DeviceFlowPanel.Visibility = Visibility.Visible;
        AuthStatusText.Visibility = Visibility.Collapsed;
        
        provider.DeviceFlowAuthRequired += info =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UserCodeText.Text = info.UserCode;
                VerificationUriLink.Content = info.VerificationUri;
                VerificationUriLink.NavigateUri = new Uri(info.VerificationUri);
                
                // Open browser automatically
                Process.Start(new ProcessStartInfo
                {
                    FileName = info.VerificationUri,
                    UseShellExecute = true
                });
            });
        };
        
        try
        {
            await provider.AuthenticateWithDeviceFlowAsync(_authCts.Token);
            
            // The provider wrote the access token into tempConfig.ApiKey
            var config = await _configService.LoadConfigAsync();
            var selectedModel = (GitHubModelComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() 
                                ?? "gpt-4o";
            
            if (!config.LlmProviders.ContainsKey("github-copilot"))
            {
                config.LlmProviders["github-copilot"] = new LlmProviderConfig
                {
                    Id = "github-copilot",
                    DisplayName = "GitHub Copilot",
                    Type = LlmProviderType.GitHubCopilot,
                    Enabled = true
                };
            }
            
            config.LlmProviders["github-copilot"].ApiKey = tempConfig.ApiKey;
            config.LlmProviders["github-copilot"].Model = selectedModel;
            config.DefaultProviderId = "github-copilot";
            
            await _configService.SaveConfigAsync(config);
            _llmService.ConfigureProviders(config);
            
            GitHubPatBox.Password = tempConfig.ApiKey;
            
            DeviceFlowPanel.Visibility = Visibility.Collapsed;
            AuthStatusText.Text = "✓ Signed in successfully!";
            AuthStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
            AuthStatusText.Visibility = Visibility.Visible;
            UpdateGitHubStatus(true);
            
            // Refresh the providers list
            await _viewModel.LoadSettingsAsync();
        }
        catch (OperationCanceledException)
        {
            DeviceFlowPanel.Visibility = Visibility.Collapsed;
            AuthStatusText.Text = "Sign-in timed out or was cancelled.";
            AuthStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
            AuthStatusText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            DeviceFlowPanel.Visibility = Visibility.Collapsed;
            AuthStatusText.Text = $"Sign-in failed: {ex.Message}";
            AuthStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
            AuthStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            GitHubSignInButton.IsEnabled = true;
        }
    }
    
    private async void GitHubPatBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        // Auto-save the PAT when entered
        var pat = GitHubPatBox.Password;
        if (string.IsNullOrWhiteSpace(pat)) return;
        
        var config = await _configService.LoadConfigAsync();
        var selectedModel = (GitHubModelComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() 
                            ?? "gpt-4o";
        
        if (!config.LlmProviders.ContainsKey("github-copilot"))
        {
            config.LlmProviders["github-copilot"] = new LlmProviderConfig
            {
                Id = "github-copilot",
                DisplayName = "GitHub Copilot",
                Type = LlmProviderType.GitHubCopilot,
                Enabled = true
            };
        }
        
        config.LlmProviders["github-copilot"].ApiKey = pat;
        config.LlmProviders["github-copilot"].Model = selectedModel;
        config.DefaultProviderId = "github-copilot";
        
        UpdateGitHubStatus(true);
    }
    
    private void GitHubModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Model selection will be saved when Save Settings is clicked
    }
    
    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.AddProviderCommand.Execute(null);
        ProvidersListView.SelectedItem = _viewModel.SelectedProvider;
        ShowProviderDetails(_viewModel.SelectedProvider);
    }
    
    private void RemoveProvider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LlmProviderConfig provider)
        {
            _viewModel.RemoveProviderCommand.Execute(provider);
        }
    }
    
    private void ProvidersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProvidersListView.SelectedItem is LlmProviderConfig provider)
        {
            _viewModel.SelectedProvider = provider;
            ShowProviderDetails(provider);
        }
        else
        {
            ProviderDetailsPanel.Visibility = Visibility.Collapsed;
        }
    }
    
    private void ShowProviderDetails(LlmProviderConfig? provider)
    {
        if (provider == null)
        {
            ProviderDetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }
        
        ProviderDetailsPanel.Visibility = Visibility.Visible;
        ProviderIdTextBox.Text = provider.Id;
        DisplayNameTextBox.Text = provider.DisplayName;
        ProviderTypeComboBox.SelectedIndex = (int)provider.Type;
        ApiKeyPasswordBox.Password = provider.ApiKey;
        ModelTextBox.Text = provider.Model;
        EndpointTextBox.Text = provider.Endpoint ?? string.Empty;
    }
    
    private void ProviderTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || ApiKeyPasswordBox == null || ModelTextBox == null) return;
        // Update placeholders based on type
        if (ProviderTypeComboBox.SelectedIndex < 0) return;
        var type = (LlmProviderType)ProviderTypeComboBox.SelectedIndex;
        switch (type)
        {
            case LlmProviderType.Ollama:
                ApiKeyPasswordBox.PlaceholderText = "(not required)";
                ModelTextBox.PlaceholderText = "llama2";
                break;
            case LlmProviderType.Anthropic:
                ApiKeyPasswordBox.PlaceholderText = "sk-ant-...";
                ModelTextBox.PlaceholderText = "claude-sonnet-4-20250514";
                break;
            default:
                ApiKeyPasswordBox.PlaceholderText = "sk-...";
                ModelTextBox.PlaceholderText = "gpt-4";
                break;
        }
    }
    
    private void UpdateSelectedProvider()
    {
        if (_viewModel.SelectedProvider == null) return;
        
        _viewModel.SelectedProvider.Id = ProviderIdTextBox.Text;
        _viewModel.SelectedProvider.DisplayName = DisplayNameTextBox.Text;
        _viewModel.SelectedProvider.Type = (LlmProviderType)ProviderTypeComboBox.SelectedIndex;
        _viewModel.SelectedProvider.ApiKey = ApiKeyPasswordBox.Password;
        _viewModel.SelectedProvider.Model = ModelTextBox.Text;
        _viewModel.SelectedProvider.Endpoint = string.IsNullOrWhiteSpace(EndpointTextBox.Text) 
            ? null 
            : EndpointTextBox.Text;
    }
    
    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectedProvider();
        
        // Also save GitHub Copilot settings from the top section
        var config = await _configService.LoadConfigAsync();
        var selectedModel = (GitHubModelComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() 
                            ?? "gpt-4o";
        
        if (config.LlmProviders.ContainsKey("github-copilot"))
        {
            config.LlmProviders["github-copilot"].Model = selectedModel;
            if (!string.IsNullOrWhiteSpace(GitHubPatBox.Password))
            {
                config.LlmProviders["github-copilot"].ApiKey = GitHubPatBox.Password;
            }
        }
        else if (!string.IsNullOrWhiteSpace(GitHubPatBox.Password))
        {
            config.LlmProviders["github-copilot"] = new LlmProviderConfig
            {
                Id = "github-copilot",
                DisplayName = "GitHub Copilot",
                Type = LlmProviderType.GitHubCopilot,
                ApiKey = GitHubPatBox.Password,
                Model = selectedModel,
                Enabled = true
            };
            config.DefaultProviderId = "github-copilot";
        }
        
        // Save other providers from ViewModel
        foreach (var provider in _viewModel.Providers)
        {
            if (provider.Type != LlmProviderType.GitHubCopilot
                && provider.Type != LlmProviderType.ChatGPT)
            {
                config.LlmProviders[provider.Id] = provider;
            }
        }
        
        // Save ChatGPT model selection if already authenticated
        var chatGptSelectedModel = (ChatGptModelComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                   ?? "gpt-4o";
        if (config.LlmProviders.ContainsKey("chatgpt"))
        {
            config.LlmProviders["chatgpt"].Model = chatGptSelectedModel;
        }
        
        await _configService.SaveConfigAsync(config);
        _llmService.ConfigureProviders(config);
        
        StatusInfoBar.Message = "Settings saved!";
        StatusInfoBar.Severity = InfoBarSeverity.Success;
        StatusInfoBar.IsOpen = true;
    }
    
    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = System.IO.Path.GetDirectoryName(_configService.GetConfigFilePath());
        if (path != null)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
    }
}
