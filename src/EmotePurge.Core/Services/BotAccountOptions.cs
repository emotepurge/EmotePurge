namespace EmotePurge.Core.Services;

/// <summary>
/// Bound from configuration section <c>BotAccount:*</c> (#487): the account-wide bot switches, deliberately not part of
/// <see cref="WishOptions"/>. A plain POCO, bound and validated once at startup and registered as a singleton for Api
/// and Worker. The token keys are not in here (the options are injectable in both processes); each entry point reads
/// its key from configuration itself.
/// </summary>
public sealed class BotAccountOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "BotAccount";

    public const int WriteDelaySecondsMin = 0;
    public const int WriteDelaySecondsMax = 60;
    public const int LeadDaysMin = 7;
    public const int LeadDaysMax = 28;
    public const int SilenceAlertHoursMin = 1;
    public const int SilenceAlertHoursMax = 168;
    public const int EditorVisibilitySecondsMin = 60;
    public const int EditorVisibilitySecondsMax = 1800;

    /// <summary>The hard account-wide switch. <c>false</c> by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Seconds between two 7TV writes of the bot (0 to 60, default 2).</summary>
    public int WriteDelaySeconds { get; set; } = 2;

    /// <summary>
    /// Seconds a fresh invitation may stay invisible in the editor read before it counts as <c>not_editor</c>
    /// (60 to 1800, default 300; no compose mapping).
    /// </summary>
    public int EditorVisibilitySeconds { get; set; } = 300;

    /// <summary>The automatic token renewal, <c>BotAccount:Renewal:*</c>.</summary>
    public BotAccountRenewalOptions Renewal { get; set; } = new();

    /// <summary>Throws unless every value is usable, naming the offending key.</summary>
    public void Validate()
    {
        RequireRange($"{SectionName}:WriteDelaySeconds", WriteDelaySeconds, WriteDelaySecondsMin, WriteDelaySecondsMax);
        RequireRange($"{SectionName}:EditorVisibilitySeconds", EditorVisibilitySeconds, EditorVisibilitySecondsMin, EditorVisibilitySecondsMax);
        RequireRange($"{SectionName}:Renewal:LeadDays", Renewal.LeadDays, LeadDaysMin, LeadDaysMax);
        RequireRange($"{SectionName}:Renewal:SilenceAlertHours", Renewal.SilenceAlertHours, SilenceAlertHoursMin, SilenceAlertHoursMax);
    }

    private static void RequireRange(string key, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            throw new InvalidOperationException($"{key} must be between {min} and {max}, but is {value}.");
        }
    }

    /// <summary>Bound from <c>BotAccount:Renewal:*</c>.</summary>
    public sealed class BotAccountRenewalOptions
    {
        /// <summary>Machine route and renewal alarms; <c>false</c> by default.</summary>
        public bool Enabled { get; set; }

        /// <summary>Days before the token's expiry at which a renewal is due (7 to 28, default 21).</summary>
        public int LeadDays { get; set; } = 21;

        /// <summary>Hours without a sign of life from the renewer before it counts as silent (1 to 168, default 26).</summary>
        public int SilenceAlertHours { get; set; } = 26;
    }
}
