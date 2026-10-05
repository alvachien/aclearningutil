namespace aclearningutil.Data.Entities;

/// <summary>
/// Per-habit invitation (FR-6 exception limited to explicitly invited users): grants
/// one user READ-ONLY visibility of the habit's definition and FULL punch history
/// through api/SharedHabits. Immediate effect — no accept step; deleting the grant
/// re-privatizes instantly (viewer queries join live).
/// </summary>
public class HabitShareGrant
{
    public int Id { get; set; }
    public int HabitId { get; set; }

    /// <summary>Owner's tenant key — denormalized like every other habit table.</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Invitee's acidserver user id (= their <c>sub</c>/<c>nameid</c> claim — the matching key).</summary>
    public string GranteeUserId { get; set; } = string.Empty;

    /// <summary>Invitee display-name snapshot at invite time (UI only; matching uses GranteeUserId).</summary>
    public string GranteeUserName { get; set; } = string.Empty;

    /// <summary>
    /// Owner display-name snapshot taken from the owner's own <c>name</c> claim at invite
    /// time (server-side, not client-sent). Serialized to viewers as ownerName.
    /// </summary>
    public string OwnerName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public Habit Habit { get; set; } = null!;
}
