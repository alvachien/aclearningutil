namespace aclearningutil.Models;

/// <summary>
/// Body of POST /api/UserLearningTimes: the category the caller is actively studying.
/// Everything else about a beat — who, when, and how many seconds it is worth — is
/// decided server-side from the token and the server clock, so the client can only
/// pick a category, never forge a duration.
/// </summary>
public sealed record LearningTimeBeatRequest
{
    /// <summary>Content category id (1-6, the seeded LearningContentCategory ids).</summary>
    public required int CategoryId { get; init; }

    /// <summary>
    /// Optional display name for the leaderboard, sent by clients that know
    /// their own OIDC user name. Purely cosmetic: the server trims/clamps it to
    /// 200 and stores it only against the CALLER's rows, so a sender can at
    /// worst mislabel itself — it can never affect anyone else's entry.
    /// </summary>
    public string? UserName { get; init; }
}

/// <summary>
/// The caller's leaderboard visibility choice (GET/PUT
/// <c>/api/UserLearningTimes/Preferences</c>). Absence of a stored row means
/// <c>false</c>: visible. Opting out hides the caller's totals from the
/// leaderboard only — their own learning-time statistics keep accumulating.
/// </summary>
public sealed record LearningTimePreferencesDto
{
    /// <summary>True = exclude the caller from the leaderboard top list.</summary>
    public required bool LeaderboardOptOut { get; init; }
}

/// <summary>
/// One ranked entry of <c>GET /api/UserLearningTimes/Leaderboard</c>: the user's
/// total credited seconds over the requested window. <c>userName</c> is the most
/// recently stored display name, falling back to a short prefix of the user id
/// for senders that never provided one.
/// </summary>
public sealed record LearningTimeLeaderboardEntry
{
    /// <summary>The JWT user id behind the totals.</summary>
    public required string UserId { get; init; }

    /// <summary>Display name, or the first 8 characters of <see cref="UserId"/> when none was stored.</summary>
    public required string UserName { get; init; }

    /// <summary>Sum of credited seconds over the window (sums to at most 92 days * 86400, far from int range).</summary>
    public required int TotalSeconds { get; init; }
}

/// <summary>
/// One (day, category) learning-time row. Returned by POST (upsert echo) and GET.
/// DateOnly serializes as "yyyy-MM-dd"; the instant carries Kind=Utc through the
/// model's read converter, so JSON ends with 'Z'. LearningSeconds is the DAY's
/// cumulative credited total for that category, not the beat's increment.
/// </summary>
public sealed record LearningTimeDto
{
    /// <summary>Server-local calendar day the row covers.</summary>
    public required DateOnly LearnDate { get; init; }

    /// <summary>Content category id (1-6).</summary>
    public required int CategoryId { get; init; }

    /// <summary>Cumulative credited learning seconds for user/day/category.</summary>
    public required int LearningSeconds { get; init; }

    /// <summary>UTC instant of the most recently accepted heartbeat.</summary>
    public required DateTime LastBeatAt { get; init; }
}
