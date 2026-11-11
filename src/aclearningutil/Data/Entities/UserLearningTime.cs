namespace aclearningutil.Data.Entities;

/// <summary>
/// Accumulated learning time, one row per user per server-local calendar day per
/// content category, upserted by POST /api/UserLearningTimes when the SPA's 60-second
/// heartbeat fires on an exercise page. LearnDate is the server-local calendar day
/// (same convention as UserLoginHistory.LoginDate / Punch.PunchDate / NFR-5);
/// LearningSeconds is the sum credited by the server's clock, not the client's;
/// LastBeatAt (UTC) is the previous accepted beat, which is what the increment is
/// measured from — so multi-tab or replayed heartbeats cannot inflate the total.
/// Deliberately has no FK/navigation: the user is the JWT "sub" string (as everywhere
/// in this database), and the category id matches LearningContentCategory but is not
/// joined (the heartbeat must stay a local, cheap write).
/// </summary>
public class UserLearningTime
{
    public int Id { get; set; }

    /// <summary>Tenant key — the caller's JWT user id.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Server-local calendar day the seconds belong to.</summary>
    public DateOnly LearnDate { get; set; }

    /// <summary>Content category (1-6, seeded LearningContentCategory ids).</summary>
    public int CategoryId { get; set; }

    /// <summary>
    /// Display name captured from the heartbeat sender (the SPA knows its own
    /// OIDC user name), overwritten by the latest non-empty beat; nullable —
    /// senders predating the field, or without a name, leave it unset. Only
    /// consumed by the leaderboard's top-list; this database still has no
    /// users table and never resolves ids to names.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>Total credited learning seconds for this user/day/category.</summary>
    public int LearningSeconds { get; set; }

    /// <summary>UTC instant of the day's most recently accepted heartbeat.</summary>
    public DateTime LastBeatAt { get; set; }
}
