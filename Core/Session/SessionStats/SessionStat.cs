using System;

using static System.Diagnostics.Stopwatch;


namespace Core;

public sealed class SessionStat
{
    public int Deaths { get; set; }
    public int Kills { get; set; }

    public long StartTime { get; set; }

    /// <summary>
    /// Set to true when vendor/repair (AdhocNPCGoal) completes successfully.
    /// Cleared when MailGoal completes successfully.
    /// Used to ensure Mail only runs after Vendor/Repair.
    /// </summary>
    public bool VendoredOrRepairedRecently { get; set; }

    // ── Death Event ──────────────────────────────────────────────────

    /// <summary>
    /// Fired when the player dies. Used by DiscordNotificationService
    /// to send death notifications to Discord.
    /// </summary>
    public event Action? OnDeath;

    /// <summary>
    /// Increments the death counter and fires the OnDeath event.
    /// Call this instead of Deaths++ so subscribers get notified.
    /// </summary>
    public void RecordDeath()
    {
        Deaths++;
        OnDeath?.Invoke();
    }

    // ── Stuck Tracking ───────────────────────────────────────────────

    // Tracks whether the bot is currently stuck (not moving)
    private bool isStuck;

    // Timestamp when the bot first became stuck
    private long stuckStartTime;

    /// <summary>
    /// How many seconds the bot has been continuously stuck.
    /// Returns 0 if not currently stuck.
    /// </summary>
    public int StuckSeconds => isStuck
        ? (int)GetElapsedTime(stuckStartTime).TotalSeconds
        : 0;

    /// <summary>
    /// Marks the bot as stuck or recovered.
    /// Starts the stuck timer on first stuck=true call;
    /// resets it when stuck=false.
    /// </summary>
    public void SetStuck(bool stuck)
    {
        if (stuck && !isStuck)
        {
            // Bot just got stuck - start timing
            isStuck = true;
            stuckStartTime = GetTimestamp();
        }
        else if (!stuck && isStuck)
        {
            // Bot recovered from being stuck
            isStuck = false;
        }
    }

    // ── Existing Accessors ───────────────────────────────────────────

    public int _Deaths() => Deaths;

    public int _Kills() => Kills;

    public int Seconds => (int)GetElapsedTime(StartTime).TotalSeconds;

    public int _Seconds() => Seconds;

    public int Minutes => (int)GetElapsedTime(StartTime).TotalMinutes;

    public int _Minutes() => Minutes;

    public int Hours => (int)GetElapsedTime(StartTime).TotalHours;

    public int _Hours() => Hours;

    public bool _VendoredOrRepairedRecently() => VendoredOrRepairedRecently;

    public void Reset()
    {
        Deaths = 0;
        Kills = 0;
        VendoredOrRepairedRecently = false;
        isStuck = false;
    }

    public void Start()
    {
        StartTime = GetTimestamp();
    }
}
