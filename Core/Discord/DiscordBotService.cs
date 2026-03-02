using Discord;
using Discord.WebSocket;

using Microsoft.Extensions.Logging;

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Discord;

/// <summary>
/// Interactive Discord bot that responds to text commands in a designated channel.
/// Connects as a Discord.Net DiscordSocketClient with gateway intents for reading messages.
///
/// Supported commands:
///   !status     - Bot status, session stats, player info
///   !stop       - Stops the bot
///   !start      - Starts the bot
///   !screenshot - Captures and sends a screenshot (!ss alias)
///   !say        - Sends a /say message in-game
///   !w          - Sends a /whisper to a player
///   !r          - Replies to the last whisperer
///   !g          - Sends a guild chat message
///   !reload     - Triggers a /reload in-game
///   !logout     - Logs the character out via /camp (!camp alias)
///   !hearth     - Uses Hearthstone
///   !help       - Shows the command list
///
/// IMPORTANT: Game commands run on a background thread via Task.Run() to avoid
/// blocking the Discord gateway thread (which would cause missed heartbeats).
/// </summary>
public sealed class DiscordBotService : IDisposable
{
    // Win32 constants for screen capture (primary monitor dimensions)
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private readonly ILogger<DiscordBotService> logger;
    private readonly IBotController botController;     // Start/stop the bot, get status
    private readonly PlayerReader playerReader;        // Player health, mana, level, XP
    private readonly BagReader bagReader;              // Bag space tracking
    private readonly LevelTracker levelTracker;        // Time-to-level estimate
    private readonly SessionStat sessionStat;          // Kills, deaths, session time
    private readonly ExecGameCommand exec;             // Sends chat commands to WoW
    private readonly DiscordNotificationService notificationService; // For LastWhisperFrom
    private readonly DiscordConfig config;
    private readonly CancellationTokenSource cts;

    private DiscordSocketClient? client;
    private bool disposed;

    public DiscordBotService(
        ILogger<DiscordBotService> logger,
        IBotController botController,
        PlayerReader playerReader,
        BagReader bagReader,
        LevelTracker levelTracker,
        SessionStat sessionStat,
        ExecGameCommand exec,
        DiscordNotificationService notificationService,
        DataConfig dataConfig,
        CancellationTokenSource cts)
    {
        this.logger = logger;
        this.botController = botController;
        this.playerReader = playerReader;
        this.bagReader = bagReader;
        this.levelTracker = levelTracker;
        this.sessionStat = sessionStat;
        this.exec = exec;
        this.notificationService = notificationService;
        this.cts = cts;

        // Load config from same file as notification service
        config = DiscordConfig.Load(dataConfig.Root);

        // Only start the bot if enabled and a token is configured
        if (config.BotEnabled && config.HasBotToken)
        {
            _ = InitializeAsync();
        }
        else
        {
            logger.LogInformation("Discord bot commands disabled (check discord_config.json)");
        }
    }

    // ── Connection ───────────────────────────────────────────────────

    /// <summary>
    /// Connects to Discord and begins listening for messages.
    /// Uses gateway intents for guild messages + message content (required for reading !commands).
    /// </summary>
    private async Task InitializeAsync()
    {
        try
        {
            DiscordSocketConfig socketConfig = new()
            {
                GatewayIntents = GatewayIntents.Guilds |
                                 GatewayIntents.GuildMessages |
                                 GatewayIntents.MessageContent
            };

            client = new DiscordSocketClient(socketConfig);

            // Wire up event handlers
            client.Log += OnLog;
            client.Ready += OnReady;
            client.MessageReceived += OnMessageReceived;

            await client.LoginAsync(TokenType.Bot, config.BotToken);
            await client.StartAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize Discord bot");
        }
    }

    /// <summary>Forwards Discord.Net internal log messages to Serilog.</summary>
    private Task OnLog(LogMessage msg)
    {
        logger.LogDebug("Discord.Net: {Message}", msg.ToString());
        return Task.CompletedTask;
    }

    /// <summary>Logged when the bot has connected and is ready to receive messages.</summary>
    private Task OnReady()
    {
        logger.LogInformation("Discord bot connected as {User}", client?.CurrentUser);
        return Task.CompletedTask;
    }

    // ── Command Router ───────────────────────────────────────────────

    /// <summary>
    /// Routes incoming Discord messages to the appropriate command handler.
    /// Only processes messages in the configured command channel (or any channel if 0).
    /// Ignores bot messages to prevent loops.
    /// </summary>
    private async Task OnMessageReceived(SocketMessage message)
    {
        // Ignore messages from other bots (prevents infinite loops)
        if (message.Author.IsBot)
            return;

        string content = message.Content.Trim();
        string contentLower = content.ToLowerInvariant();

        // All commands start with '!'
        if (!contentLower.StartsWith('!'))
            return;

        try
        {
            // Handle commands that take arguments first (prefix matching)
            if (contentLower.StartsWith("!say "))
            {
                await HandleSayCommand(message.Channel, content[5..]);
                return;
            }
            if (contentLower.StartsWith("!w "))
            {
                await HandleWhisperCommand(message.Channel, content[3..]);
                return;
            }
            if (contentLower.StartsWith("!r "))
            {
                await HandleReplyCommand(message.Channel, content[3..]);
                return;
            }
            if (contentLower.StartsWith("!g "))
            {
                await HandleGuildCommand(message.Channel, content[3..]);
                return;
            }

            // Exact-match commands (no arguments)
            switch (contentLower)
            {
                case "!status":
                    await HandleStatusCommand(message.Channel);
                    break;
                case "!stop":
                    await HandleStopCommand(message.Channel);
                    break;
                case "!start":
                    await HandleStartCommand(message.Channel);
                    break;
                case "!screenshot":
                case "!ss":
                    await HandleScreenshotCommand(message.Channel);
                    break;
                case "!reload":
                    await HandleReloadCommand(message.Channel);
                    break;
                case "!hearth":
                    await HandleHearthCommand(message.Channel);
                    break;
                case "!logout":
                case "!camp":
                    await HandleLogoutCommand(message.Channel);
                    break;
                case "!help":
                    await HandleHelpCommand(message.Channel);
                    break;
                default:
                    // Unknown command - show help
                    await HandleHelpCommand(message.Channel);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling Discord command: {Command}", content);
            await message.Channel.SendMessageAsync($"Error: {ex.Message}");
        }
    }

    // ── Command Handlers ─────────────────────────────────────────────

    /// <summary>
    /// Sends a rich embed with bot status, player info, and session stats.
    /// Green embed = running, Red embed = stopped.
    /// </summary>
    private async Task HandleStatusCommand(ISocketMessageChannel channel)
    {
        string status = botController.IsBotActive ? "Running" : "Stopped";
        string className = botController.ClassConfig?.FileName ?? "None";

        // Sum free slots across all general-purpose bags (exclude special bags like ammo)
        int freeSlots = bagReader.Bags
            .Where(b => b.BagType == BagType.Unspecified)
            .Sum(b => b.FreeSlot);
        int totalSlots = bagReader.Bags
            .Where(b => b.BagType == BagType.Unspecified)
            .Sum(b => b.SlotCount);

        // Format time to level estimate
        TimeSpan ttl = levelTracker.TimeToLevel;
        string timeToLevel = ttl > TimeSpan.Zero
            ? $"{(int)ttl.TotalHours}h {ttl.Minutes}m"
            : "N/A";

        // Build the embed - green when running, red when stopped
        // NOTE: Use global::Discord.Color to avoid conflict with System.Drawing.Color
        EmbedBuilder embed = new EmbedBuilder()
            .WithTitle("Bot Status")
            .WithColor(botController.IsBotActive
                ? new global::Discord.Color(0x2E, 0xCC, 0x71)   // green
                : new global::Discord.Color(0xE7, 0x4C, 0x3C))  // red
            .AddField("Status", status, true)
            .AddField("Profile", className, true)
            .AddField("Level", playerReader.Level.Value.ToString(), true)
            .AddField("XP Progress", $"{playerReader.PlayerXpPercent}%", true)
            .AddField("Time to Level", timeToLevel, true)
            .AddField("Bag Space", $"{freeSlots}/{totalSlots} free", true)
            .AddField("HP / Mana", $"{playerReader.HealthPercent()}% / {playerReader.ManaPercent()}%", true)
            .AddField("Kills / Deaths", $"{sessionStat.Kills} / {sessionStat.Deaths}", true)
            .AddField("Session", $"{sessionStat.Minutes} min", true)
            .WithTimestamp(DateTimeOffset.Now);

        await channel.SendMessageAsync(embed: embed.Build());
    }

    /// <summary>Stops the bot if it's currently running.</summary>
    private async Task HandleStopCommand(ISocketMessageChannel channel)
    {
        if (!botController.IsBotActive)
        {
            await channel.SendMessageAsync("Bot is already stopped.");
            return;
        }

        botController.ToggleBotStatus();
        await channel.SendMessageAsync("Bot stopped.");
    }

    /// <summary>Starts the bot if it's currently stopped.</summary>
    private async Task HandleStartCommand(ISocketMessageChannel channel)
    {
        if (botController.IsBotActive)
        {
            await channel.SendMessageAsync("Bot is already running.");
            return;
        }

        botController.ToggleBotStatus();
        await channel.SendMessageAsync("Bot started.");
    }

    /// <summary>Captures a screenshot and uploads it to the Discord channel.</summary>
    private async Task HandleScreenshotCommand(ISocketMessageChannel channel)
    {
        byte[] screenshotBytes = CaptureScreenshot();
        if (screenshotBytes.Length == 0)
        {
            await channel.SendMessageAsync("Failed to capture screenshot.");
            return;
        }

        logger.LogInformation("Sending screenshot to Discord");
        using MemoryStream ms = new(screenshotBytes);
        await channel.SendFileAsync(ms, "screenshot.jpg", "Current screen:");
    }

    /// <summary>Sends a /say message in the game chat.</summary>
    private async Task HandleSayCommand(ISocketMessageChannel channel, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await channel.SendMessageAsync("Usage: !say <message>");
            return;
        }

        await SendGameChatAsync($"/say {text}");
        await channel.SendMessageAsync($"Sent /say: {text}");
    }

    /// <summary>Sends a /whisper to a specific player. Format: !w PlayerName message</summary>
    private async Task HandleWhisperCommand(ISocketMessageChannel channel, string args)
    {
        // Expected format: "PlayerName message text here"
        int spaceIndex = args.IndexOf(' ');
        if (spaceIndex == -1)
        {
            await channel.SendMessageAsync("Usage: !w <player> <message>");
            return;
        }

        string playerName = args[..spaceIndex].Trim();
        string message = args[(spaceIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(message))
        {
            await channel.SendMessageAsync("Usage: !w <player> <message>");
            return;
        }

        await SendGameChatAsync($"/w {playerName} {message}");
        await channel.SendMessageAsync($"Whispered to {playerName}: {message}");
    }

    /// <summary>
    /// Replies to the last player who whispered us.
    /// Uses LastWhisperFrom from the notification service.
    /// </summary>
    private async Task HandleReplyCommand(ISocketMessageChannel channel, string text)
    {
        string? lastWhisperer = notificationService.LastWhisperFrom;
        if (string.IsNullOrEmpty(lastWhisperer))
        {
            await channel.SendMessageAsync("No recent whispers to reply to.");
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            await channel.SendMessageAsync("Usage: !r <message>");
            return;
        }

        await SendGameChatAsync($"/w {lastWhisperer} {text}");
        await channel.SendMessageAsync($"Replied to {lastWhisperer}: {text}");
    }

    /// <summary>Sends a message to guild chat.</summary>
    private async Task HandleGuildCommand(ISocketMessageChannel channel, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await channel.SendMessageAsync("Usage: !g <message>");
            return;
        }

        await SendGameChatAsync($"/g {text}");
        await channel.SendMessageAsync($"Sent to guild: {text}");
    }

    /// <summary>Triggers a /reload in-game (reloads the WoW UI/addons).</summary>
    private async Task HandleReloadCommand(ISocketMessageChannel channel)
    {
        await SendGameChatAsync("/reload");
        await channel.SendMessageAsync("Reload triggered.");
    }

    /// <summary>
    /// Stops the bot first (if running), then sends /camp to log out.
    /// IMPORTANT: Uses ToggleBotStatus() NOT Shutdown() - Shutdown() kills the entire app.
    /// </summary>
    private async Task HandleLogoutCommand(ISocketMessageChannel channel)
    {
        // Stop the bot first so it doesn't interfere with the logout
        if (botController.IsBotActive)
            botController.ToggleBotStatus();

        await SendGameChatAsync("/camp");
        await channel.SendMessageAsync("Logging out... (bot stopped, /camp sent)");
    }

    /// <summary>
    /// Stops the bot first (if running), then uses Hearthstone.
    /// Bot stays stopped so it doesn't interfere with the hearth cast.
    /// </summary>
    private async Task HandleHearthCommand(ISocketMessageChannel channel)
    {
        // Stop the bot first so it doesn't move/act during hearth cast
        if (botController.IsBotActive)
            botController.ToggleBotStatus();

        await SendGameChatAsync("/use Hearthstone");
        await channel.SendMessageAsync("Using Hearthstone... (bot stopped)");
    }

    /// <summary>Shows all available commands as a Discord embed.</summary>
    private static async Task HandleHelpCommand(ISocketMessageChannel channel)
    {
        // Use Discord blurple color for the help embed
        EmbedBuilder embed = new EmbedBuilder()
            .WithTitle("WoW Bot Commands")
            .WithColor(new global::Discord.Color(0x58, 0x65, 0xF2))
            .WithDescription(
                "**!start**\nStart the bot\n\n" +
                "**!stop**\nStop the bot\n\n" +
                "**!status**\nGet current bot status (incl. bag space)\n\n" +
                "**!screenshot / !ss**\nTake and send a screenshot\n\n" +
                "**!say <message>**\nSend a /say message in-game\n\n" +
                "**!w <name> <message>**\nWhisper a player\n\n" +
                "**!r <message>**\nReply to last whisper\n\n" +
                "**!g <message>**\nSend guild chat message\n\n" +
                "**!reload**\nReload WoW UI\n\n" +
                "**!hearth**\nUse Hearthstone\n\n" +
                "**!logout / !camp**\nStop bot and logout (/camp)\n\n" +
                "**!help**\nShow this help message");

        await channel.SendMessageAsync(embed: embed.Build());
    }

    // ── Chat Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Sends a chat command to WoW via ExecGameCommand.
    /// Pauses the bot while typing so it doesn't interfere, then restarts it.
    /// Runs on a background thread so the Discord gateway is NOT blocked.
    /// ExecGameCommand.Run() handles SetForegroundWindow + SendText internally.
    /// </summary>
    private Task SendGameChatAsync(string command)
    {
        return Task.Run(() =>
        {
            // Pause the bot so it doesn't act while we're typing in chat
            bool wasActive = botController.IsBotActive;
            if (wasActive)
                botController.ToggleBotStatus();

            exec.Run(command);

            // Resume the bot after the command is sent
            if (wasActive)
                botController.ToggleBotStatus();
        });
    }

    // ── Screenshot ───────────────────────────────────────────────────

    /// <summary>
    /// Captures the primary screen as a JPEG byte array using GDI+.
    /// Returns an empty array if capture fails.
    /// </summary>
    private static byte[] CaptureScreenshot()
    {
        try
        {
            int width = GetSystemMetrics(SM_CXSCREEN);
            int height = GetSystemMetrics(SM_CYSCREEN);

            using Bitmap bitmap = new(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(0, 0, 0, 0, new Size(width, height));

            using MemoryStream ms = new();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
            return ms.ToArray();
        }
        catch
        {
            return [];
        }
    }

    // ── Cleanup ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        if (client != null)
        {
            // Unsubscribe from events before disposing
            client.Log -= OnLog;
            client.Ready -= OnReady;
            client.MessageReceived -= OnMessageReceived;
            client.Dispose();
        }
    }
}
