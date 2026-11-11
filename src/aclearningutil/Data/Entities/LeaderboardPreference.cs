namespace aclearningutil.Data.Entities;

/// <summary>
/// One user's leaderboard visibility choice: a row exists once the caller has
/// ever touched the "hide me" switch; absence means visible. Deliberately
/// separate from <see cref="UserLearningTime"/> — opting out hides the user's
/// totals from GET …/Leaderboard only; their own learning-time statistics
/// keep accumulating and remain visible to themselves on the user-detail page.
/// Deliberately has no FK/navigation: the user is the JWT "sub" string, as
/// everywhere in this database.
/// </summary>
public class LeaderboardPreference
{
    public int Id { get; set; }

    /// <summary>Tenant key — the caller's JWT user id.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>True = exclude this user's totals from the leaderboard.</summary>
    public bool LeaderboardOptOut { get; set; }
}
