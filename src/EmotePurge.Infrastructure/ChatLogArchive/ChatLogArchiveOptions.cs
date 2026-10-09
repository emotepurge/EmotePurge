namespace EmotePurge.Infrastructure.ChatLogArchive;

/// <summary>
/// Bound from configuration section <c>ChatLogArchive:*</c>. A plain POCO rather than an
/// <c>IOptions</c>-wrapped type — the HttpClient registration needs <see cref="BaseUrl"/> already
/// bound at registration time to set <c>HttpClient.BaseAddress</c>, not behind a snapshot
/// indirection.
/// </summary>
public sealed class ChatLogArchiveOptions
{
    /// <summary>
    /// A justlog-compatible archive (format measured live 2026-09-05, T8). Defaults to
    /// <c>logs.cyex.app</c> since 2026-10-08, when the previous default (<c>logs.zonian.dev</c>) went
    /// offline. Required on api, worker and harness alike: a backfill run stores the value it was
    /// requested against and the worker refuses a run whose value differs from its own.
    /// </summary>
    public string BaseUrl { get; set; } = "https://logs.cyex.app/";

    /// <summary>
    /// Minimum time between the start of consecutive requests. A self-imposed courtesy pace, not a
    /// measured limit — the archive documents no contract and sends no rate-limit headers
    /// (Premise 5 of the design).
    /// </summary>
    public TimeSpan RequestDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Deadline for the response body to finish once headers have arrived. Separate from
    /// <c>HttpClient.Timeout</c>, which <c>HttpCompletionOption.ResponseHeadersRead</c> makes cover
    /// only the header phase.
    /// </summary>
    public TimeSpan BodyTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Body deadline for <c>ReadRangeAsync</c>: a weekly block of up to ~66 MB at the measured
    /// 13-27 MB/s needs under 6 s, so 5 minutes tolerates a link ten times slower.
    /// </summary>
    public TimeSpan RangeBodyTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The longest decoded line <c>ReadRangeAsync</c> accepts. A Twitch IRC line is at most 512 bytes
    /// of text plus tags (measured lines stay well under 4 KiB); a longer one ends the read with
    /// <c>LineTooLong</c> so a delimiter-free body can never be buffered as one line.
    /// </summary>
    public int MaxLineBytes { get; set; } = 16 * 1024;
}
