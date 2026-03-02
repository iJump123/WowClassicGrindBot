using Microsoft.Extensions.Logging;

using System;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Rectangle = System.Drawing.Rectangle;

namespace Core.Discord;

/// <summary>
/// Sends Discord webhook notifications for in-game events:
///   - Chat messages (whisper, say, yell, etc.)
///   - Player deaths
///   - Bot stuck detection (stuck > 30 seconds)
///   - Startup notification
///
/// Subscribes to TextReader.Messages and SessionStat.OnDeath on construction.
/// Runs a periodic timer to check for stuck state.
/// </summary>
public sealed class DiscordNotificationService : IDisposable
{
    // Win32 constants for GetSystemMetrics to get screen dimensions
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// How many seconds the bot must be stuck before sending a notification.
    /// Prevents spamming Discord for brief pauses.
    /// </summary>
    private const int STUCK_THRESHOLD_SECONDS = 30;

    /// <summary>
    /// How often to check if the bot is stuck (milliseconds).
    /// Checks every 5 seconds for responsiveness without being excessive.
    /// </summary>
    private const int STUCK_CHECK_INTERVAL_MS = 5000;

    private readonly ILogger<DiscordNotificationService> logger;
    private readonly TextReader textReader;       // Provides chat messages via ObservableCollection
    private readonly SessionStat sessionStat;     // Tracks deaths and stuck state
    private readonly DiscordConfig config;        // Webhook URL, toggles, etc.
    private readonly HttpClient httpClient;       // Reused for all webhook POST requests
    private readonly CancellationTokenSource cts; // App-wide cancellation
    private readonly Timer? stuckCheckTimer;       // Periodic stuck-detection timer

    private bool disposed;

    // True if we already sent a "stuck" alert - prevents spamming
    private bool stuckNotificationSent;

    /// <summary>
    /// The name of the last player who whispered us.
    /// Used by DiscordBotService and Chat.razor for /reply functionality.
    /// </summary>
    public string? LastWhisperFrom { get; private set; }

    public DiscordNotificationService(
        ILogger<DiscordNotificationService> logger,
        TextReader textReader,
        SessionStat sessionStat,
        DataConfig dataConfig,
        CancellationTokenSource cts)
    {
        this.logger = logger;
        this.textReader = textReader;
        this.sessionStat = sessionStat;
        this.cts = cts;

        // Load config from Json/Discord/discord_config.json
        config = DiscordConfig.Load(dataConfig.Root);
        httpClient = new HttpClient();

        if (config.Enabled && config.HasWebhook)
        {
            // Subscribe to chat messages so we can forward them to Discord
            textReader.Messages.CollectionChanged += OnChatMessageReceived;

            // Subscribe to player death events for death alerts
            sessionStat.OnDeath += OnPlayerDeath;

            // Periodically check if the bot is stuck and alert if so
            stuckCheckTimer = new Timer(
                CheckStuckStatus, null,
                STUCK_CHECK_INTERVAL_MS, STUCK_CHECK_INTERVAL_MS);

            // Send a startup notification with screenshot after a brief delay
            if (config.NotifyOnStartup)
            {
                _ = SendStartupNotificationAsync();
            }

            logger.LogInformation("Discord notifications enabled");
        }
        else
        {
            logger.LogInformation("Discord notifications disabled (check discord_config.json)");
        }
    }

    // ── Startup ──────────────────────────────────────────────────────

    /// <summary>
    /// Sends a startup notification with screenshot to confirm the bot is online.
    /// Delays 3 seconds to let the application fully initialize before capturing.
    /// </summary>
    private async Task SendStartupNotificationAsync()
    {
        try
        {
            // Wait for the app to finish initializing before taking a screenshot
            await Task.Delay(3000, cts.Token);

            using MultipartFormDataContent content = new();
            AddPayload(content,
                "\ud83d\ude80 **Bot Started!**\nDiscord notifications are active.");
            AttachScreenshot(content, "startup");

            await PostWebhookAsync(content, "startup notification");
        }
        catch (OperationCanceledException) { /* Shutdown requested - ignore */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Discord startup notification");
        }
    }

    // ── Chat Message Forwarding ──────────────────────────────────────

    /// <summary>
    /// Handles new chat messages from the addon (via TextReader).
    /// Filters by type based on user preferences before forwarding to Discord.
    /// Also tracks the last whisperer for reply support.
    /// </summary>
    private void OnChatMessageReceived(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null)
            return;

        foreach (ChatMessageEntry entry in e.NewItems)
        {
            // Track the last player who whispered us for /reply
            if (entry.Type == ChatMessageType.Whisper)
            {
                LastWhisperFrom = entry.Author;
            }

            // Only forward to Discord if this message type is enabled in config
            if (config.ShouldNotify(entry.Type))
            {
                _ = SendNotificationAsync(entry);
            }
        }
    }

    // ── Death Notification ───────────────────────────────────────────

    /// <summary>
    /// Called when the player dies. Sends a death alert to Discord.
    /// </summary>
    private void OnPlayerDeath()
    {
        _ = SendDeathNotificationAsync();
    }

    // ── Stuck Detection ──────────────────────────────────────────────

    /// <summary>
    /// Timer callback: checks if the bot is stuck and sends a one-time alert.
    /// Resets the alert flag when the bot recovers (StuckSeconds returns to 0).
    /// </summary>
    private void CheckStuckStatus(object? state)
    {
        int stuckSeconds = sessionStat.StuckSeconds;

        if (stuckSeconds >= STUCK_THRESHOLD_SECONDS && !stuckNotificationSent)
        {
            // Bot is stuck past threshold - send alert (only once)
            stuckNotificationSent = true;
            _ = SendStuckNotificationAsync(stuckSeconds);
        }
        else if (stuckSeconds == 0 && stuckNotificationSent)
        {
            // Bot recovered from being stuck - reset so we can alert again next time
            stuckNotificationSent = false;
        }
    }

    // ── Notification Senders ─────────────────────────────────────────

    /// <summary>
    /// Sends a stuck notification with screenshot to the Discord webhook.
    /// </summary>
    private async Task SendStuckNotificationAsync(int stuckSeconds)
    {
        if (cts.IsCancellationRequested)
            return;

        try
        {
            string messageContent =
                $"\u26a0\ufe0f **BOT STUCK** for {stuckSeconds} seconds!";

            using MultipartFormDataContent content = new();
            AddPayload(content, messageContent);
            AttachScreenshot(content, "stuck");

            await PostWebhookAsync(content, "stuck notification");
        }
        catch (OperationCanceledException) { /* Shutdown requested */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Discord stuck notification");
        }
    }

    /// <summary>
    /// Sends a death notification with the total death count for this session.
    /// </summary>
    private async Task SendDeathNotificationAsync()
    {
        if (cts.IsCancellationRequested)
            return;

        try
        {
            string messageContent =
                $"\u2620\ufe0f **YOU DIED!**\nTotal deaths this session: {sessionStat.Deaths}";

            using MultipartFormDataContent content = new();
            AddPayload(content, messageContent);
            AttachScreenshot(content, "death");

            await PostWebhookAsync(content, "death notification");
        }
        catch (OperationCanceledException) { /* Shutdown requested */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Discord death notification");
        }
    }

    /// <summary>
    /// Sends a chat message notification with emoji-coded type and optional screenshot.
    /// </summary>
    private async Task SendNotificationAsync(ChatMessageEntry entry)
    {
        if (cts.IsCancellationRequested)
            return;

        try
        {
            // Map each chat type to an emoji for visual distinction in Discord
            string emoji = entry.Type switch
            {
                ChatMessageType.Whisper => "\ud83d\udcac",    // speech bubble
                ChatMessageType.Say => "\ud83d\udde3\ufe0f",  // speaking head
                ChatMessageType.Yell => "\ud83d\udce2",       // loudspeaker
                ChatMessageType.Emote => "\ud83c\udfad",      // performing arts
                ChatMessageType.Party => "\ud83d\udc65",      // busts in silhouette
                ChatMessageType.Guild => "\ud83d\udee1\ufe0f",// shield
                _ => "\u2709\ufe0f"                            // envelope
            };

            string typeLabel = entry.Type.ToString().ToUpperInvariant();
            string messageContent =
                $"{emoji} **{typeLabel}** from **{entry.Author}**\n> {entry.Message}";

            using MultipartFormDataContent content = new();
            AddPayload(content, messageContent);

            // Only attach screenshot if the user wants it
            if (config.IncludeScreenshot)
            {
                AttachScreenshot(content, "screenshot");
            }

            await PostWebhookAsync(content, $"{entry.Type} from {entry.Author}");
        }
        catch (OperationCanceledException) { /* Shutdown requested */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Discord notification");
        }
    }

    /// <summary>
    /// Sends a test message to verify the webhook URL works.
    /// Called from the Discord configuration UI page.
    /// </summary>
    public async Task SendTestNotification()
    {
        if (!config.Enabled || string.IsNullOrEmpty(config.WebhookUrl))
        {
            logger.LogWarning("Cannot send test: Discord notifications are disabled");
            return;
        }

        try
        {
            using MultipartFormDataContent content = new();
            AddPayload(content,
                "\u2705 **WoW Bot Connected!**\nDiscord notifications are working.");

            if (config.IncludeScreenshot)
            {
                AttachScreenshot(content, "test_screenshot");
            }

            await PostWebhookAsync(content, "test message");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Discord test message");
        }
    }

    // ── Helper Methods ───────────────────────────────────────────────

    /// <summary>
    /// Adds the JSON payload (message text + username) to the multipart content.
    /// Discord webhooks expect a "payload_json" field with the message body.
    /// </summary>
    private static void AddPayload(MultipartFormDataContent content, string message)
    {
        var payload = new { content = message, username = "WoW Bot Alert" };
        string json = JsonSerializer.Serialize(payload);
        content.Add(new StringContent(json, Encoding.UTF8, "application/json"), "payload_json");
    }

    /// <summary>
    /// Captures a screenshot and attaches it to the multipart content.
    /// Silently skips if screenshot capture fails (e.g. headless environment).
    /// </summary>
    private void AttachScreenshot(MultipartFormDataContent content, string prefix)
    {
        try
        {
            byte[] bytes = CaptureScreenshot();
            if (bytes.Length > 0)
            {
                ByteArrayContent imageContent = new(bytes);
                imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                // Unique filename with timestamp to avoid Discord caching
                content.Add(imageContent, "file", $"{prefix}_{DateTime.Now:HHmmss}.jpg");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to capture screenshot for {Prefix}", prefix);
        }
    }

    /// <summary>
    /// Posts the multipart content to the configured webhook URL.
    /// Logs success or failure for debugging.
    /// </summary>
    private async Task PostWebhookAsync(MultipartFormDataContent content, string description)
    {
        HttpResponseMessage response = await httpClient.PostAsync(
            config.WebhookUrl, content, cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            logger.LogWarning("Discord webhook returned {StatusCode}: {Body}",
                response.StatusCode, body);
        }
        else
        {
            logger.LogInformation("Discord {Description} sent", description);
        }
    }

    // ── Screenshot Capture ───────────────────────────────────────────

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

            using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(0, 0, 0, 0, new Size(width, height));

            using MemoryStream ms = new();
            bitmap.Save(ms, ImageFormat.Jpeg);
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

        // Stop the periodic stuck-check timer
        stuckCheckTimer?.Dispose();

        // Unsubscribe from events to prevent memory leaks
        textReader.Messages.CollectionChanged -= OnChatMessageReceived;
        sessionStat.OnDeath -= OnPlayerDeath;

        httpClient.Dispose();
    }
}
