namespace aclearningutil.Models;

/// <summary>
/// One day of the caller's login history. Returned by both POST (upsert echo) and GET.
/// DateOnly serializes as "yyyy-MM-dd"; the instants carry Kind=Utc through the model's
/// read converter, so JSON ends with 'Z'.
/// POST takes no request body: the user comes from the token and the clock from the
/// server, so neither can be forged by the client.
/// </summary>
public sealed record UserLoginHistoryDto
{
    /// <summary>Server-local calendar day the row covers.</summary>
    public required DateOnly LoginDate { get; init; }

    /// <summary>UTC instant of the day's first recorded sign-in.</summary>
    public required DateTime FirstLoginAt { get; init; }

    /// <summary>UTC instant of the day's most recent recorded sign-in.</summary>
    public required DateTime LastLoginAt { get; init; }

    /// <summary>Number of recorded sign-ins on that day.</summary>
    public required int LoginCount { get; init; }
}
