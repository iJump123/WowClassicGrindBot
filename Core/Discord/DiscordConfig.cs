using Newtonsoft.Json;

using System.IO;

namespace Core.Discord;

/// <summary>
/// Configuration for Discord webhook notifications and bot commands.
/// Loaded from Json/Discord/discord_config.json relative to the data root.
/// Contains webhook URL, bot token, and notification toggle settings.
/// </summary>
public sealed class DiscordConfig
{
    // Subfolder under the JSON root where config is stored
    private const string ConfigDirectory = "Discord";

    // Name of the JSON config file
    private const string ConfigFileName = "discord_config.json";

    /// <summary>
    /// Root path used for saving - not serialized to JSON.
    /// Set during Load() so Save() knows where to write.
    /// </summary>
    [JsonIgnore]
    private string? _rootPath;

    // ── Webhook Settings ─────────────────────────────────────────────

    /// <summary>
    /// Discord webhook URL for sending notifications.
    /// Create one in Discord: Channel Settings > Integrations > Webhooks.
    /// </summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Master toggle for webhook notifications.
    /// When false, no webhook messages are sent regardless of other settings.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether to attach a screenshot with each notification.
    /// Captures the primary monitor as a JPEG.
    /// </summary>
    public bool IncludeScreenshot { get; set; } = true;

    // ── Notification Triggers ────────────────────────────────────────
    // Each toggle controls whether that chat type is forwarded to Discord.

    /// <summary>
    /// Send a notification with screenshot when the bot starts up.
    /// </summary>
    public bool NotifyOnStartup { get; set; } = true;

    public bool NotifyOnWhisper { get; set; } = true;
    public bool NotifyOnSay { get; set; } = true;
    public bool NotifyOnYell { get; set; } = true;
    public bool NotifyOnEmote { get; set; }
    public bool NotifyOnParty { get; set; }
    public bool NotifyOnGuild { get; set; }

    // ── Bot Settings ─────────────────────────────────────────────────

    /// <summary>
    /// Discord bot token from the Developer Portal.
    /// Required for interactive bot commands (!status, !stop, etc.).
    /// Keep this secret - never commit to source control.
    /// </summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Whether the interactive bot is enabled.
    /// Requires a valid BotToken to function.
    /// </summary>
    public bool BotEnabled { get; set; }

    // ── Computed Properties ──────────────────────────────────────────

    /// <summary>True if BotToken is a non-empty, non-whitespace string.</summary>
    [JsonIgnore]
    public bool HasBotToken => !string.IsNullOrWhiteSpace(BotToken);

    /// <summary>True if WebhookUrl is a non-empty, non-whitespace string.</summary>
    [JsonIgnore]
    public bool HasWebhook => !string.IsNullOrWhiteSpace(WebhookUrl);

    // ── Load / Save ──────────────────────────────────────────────────

    /// <summary>
    /// Loads the Discord configuration from disk.
    /// Creates the directory and returns a default config if file doesn't exist.
    /// </summary>
    /// <param name="rootPath">The JSON root directory (e.g. ../json).</param>
    public static DiscordConfig Load(string rootPath)
    {
        // Ensure the Discord config directory exists
        string directory = Path.Combine(rootPath, ConfigDirectory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, ConfigFileName);

        DiscordConfig config;
        if (!File.Exists(path))
        {
            // No config file yet - return defaults
            config = new DiscordConfig();
        }
        else
        {
            try
            {
                string json = File.ReadAllText(path);
                config = JsonConvert.DeserializeObject<DiscordConfig>(json) ?? new DiscordConfig();
            }
            catch
            {
                // Corrupted or unreadable config - return defaults
                config = new DiscordConfig();
            }
        }

        // Remember root path so Save() knows where to write
        config._rootPath = rootPath;
        return config;
    }

    /// <summary>
    /// Saves the current configuration to disk as formatted JSON.
    /// </summary>
    public void Save()
    {
        if (string.IsNullOrEmpty(_rootPath))
            return;

        string directory = Path.Combine(_rootPath, ConfigDirectory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, ConfigFileName);
        string json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Checks whether a given chat message type should trigger a notification
    /// based on the current notification trigger settings.
    /// </summary>
    /// <param name="type">The type of chat message received.</param>
    /// <returns>True if this type should send a Discord notification.</returns>
    public bool ShouldNotify(ChatMessageType type)
    {
        return type switch
        {
            ChatMessageType.Whisper => NotifyOnWhisper,
            ChatMessageType.Say => NotifyOnSay,
            ChatMessageType.Yell => NotifyOnYell,
            ChatMessageType.Emote => NotifyOnEmote,
            ChatMessageType.Party => NotifyOnParty,
            ChatMessageType.Guild => NotifyOnGuild,
            _ => false
        };
    }
}
