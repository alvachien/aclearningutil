namespace aclearningutil.Data.Entities;

/// <summary>
/// One row per user per calendar day, upserted by POST /api/UserLoginHistories when the
/// SPA completes an OIDC sign-in. LoginDate is the server-local calendar day (same
/// convention as Punch.PunchDate / NFR-5); FirstLoginAt/LastLoginAt are UTC instants;
/// LoginCount is the number of recorded sign-ins on that day.
/// Deliberately has no FK/navigation: the user is the JWT "sub" string, as everywhere
/// in this database (no Identity tables here).
/// </summary>
public class UserLoginHistory
{
    public int Id { get; set; }

    /// <summary>Tenant key — the caller's JWT user id.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Server-local calendar day the logins belong to (ISO yyyy-MM-dd at storage level).</summary>
    public DateOnly LoginDate { get; set; }

    /// <summary>UTC instant of the day's first recorded sign-in; immutable once created.</summary>
    public DateTime FirstLoginAt { get; set; }

    /// <summary>UTC instant of the day's most recent recorded sign-in.</summary>
    public DateTime LastLoginAt { get; set; }

    /// <summary>Number of recorded sign-ins on that day.</summary>
    public int LoginCount { get; set; }
}
