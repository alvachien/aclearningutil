using FluentAssertions;
using aclearningutil.Controllers;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using aclearningutil.Services;
using aclearningutil.test.Helpers;
using Microsoft.EntityFrameworkCore;

namespace aclearningutil.test.Controllers;

/// <summary>
/// The read-only "shared with me" surface (FR-6 exception limited to named invitees):
/// an invited user sees the habit definition, items/criteria and FULL punch history;
/// everyone else — including owners of unrelated habits and the third user — gets
/// indistinguishable 404s. Owner identity never surfaces as a claim id, only as the
/// name snapshot captured server-side at invite time.
/// </summary>
public class SharedHabitsControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly SharedHabitsController _controller;
    private const string Owner = HabitTestData.TestUser;
    private const string Viewer = HabitTestData.OtherUser;
    private const string Stranger = HabitTestData.ThirdUser;
    private const string OwnerDisplayName = "Alice";

    public SharedHabitsControllerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
        var evaluation = new HabitEvaluationService(_context);
        _controller = new SharedHabitsController(_context, evaluation);
        // The caller is the invitee — not the owner.
        HabitTestData.SetupUserClaims(_controller, Viewer);
    }

    public void Dispose() => _context.Dispose();

    /// <summary>Seeds an owned habit and invites the viewer (grant + name snapshot).</summary>
    private async Task<(Habit habit, HabitItem item, ItemProperty property)> SeedInvitedAsync(
        HabitState state = HabitState.Active, string ownerName = OwnerDisplayName)
    {
        var (habit, item, property, _) = await HabitTestData.SeedMinimalRunningAsync(_context, Owner, state: state);
        await HabitTestData.SeedGrantAsync(_context, habit, Viewer, "Bob", ownerName);
        return (habit, item, property);
    }

    private static async Task<string> ExpectCodeAsync(Func<Task> act)
    {
        var ex = await Assert.ThrowsAsync<HabitException>(act);
        return ex.Code;
    }

    // ── GET /api/SharedHabits — "shared with me" gallery ─────────────────────────────

    [Fact]
    public async Task Gallery_Shows_Only_Invited_Habits_With_OwnerName()
    {
        await SeedInvitedAsync();
        // A second, unrelated habit of the same owner WITHOUT a grant stays invisible.
        await HabitTestData.SeedMinimalRunningAsync(_context, Owner);

        var result = await _controller.GetAll(CancellationToken.None);

        var entry = result.Value.Should().ContainSingle().Subject;
        entry.OwnerName.Should().Be(OwnerDisplayName);
        entry.Habit.Name.Should().Be("Seeded habit");
        entry.Habit.Progress.RootCriterion.Name.Should().Be("C1");
        typeof(HabitOutDto).GetProperty("OwnerId").Should().BeNull(); // claim id never serialized
    }

    [Fact]
    public async Task Gallery_Empty_Without_Grants()
    {
        await HabitTestData.SeedMinimalRunningAsync(_context, Owner);

        var result = await _controller.GetAll(CancellationToken.None);
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Gallery_Does_Not_Leak_The_Owners_Own_List_Through_The_Viewer_Endpoint()
    {
        await SeedInvitedAsync();
        HabitTestData.SetupUserClaims(_controller, Owner, OwnerDisplayName);

        // The owner's own habits live behind GET /api/Habits; this endpoint is grant-only.
        var result = await _controller.GetAll(CancellationToken.None);
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Gallery_Includes_Inactive_But_Invited_Habits()
    {
        await SeedInvitedAsync(HabitState.Inactive);

        var entry = (await _controller.GetAll(CancellationToken.None)).Value!.Single();
        entry.Habit.State.Should().Be(HabitState.Inactive);
    }

    // ── GET /api/SharedHabits/{id} ───────────────────────────────────────────────────

    [Fact]
    public async Task Detail_Returns_Items_With_Aggregates_And_Criteria()
    {
        var (habit, item, property) = await SeedInvitedAsync();
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, HabitTestData.Today, numValue: 4.0);

        var detail = (await _controller.GetById(habit.Id, CancellationToken.None)).Value!;

        detail.OwnerName.Should().Be(OwnerDisplayName);
        detail.Items.Should().ContainSingle();
        detail.Items[0].Properties[0].CurrentCycleValue.Should().Be(4.0);
        detail.Items[0].Properties[0].TodayValue.Should().Be(4.0);
        detail.Items[0].HasPunches.Should().BeTrue();
        detail.Criteria.Should().ContainSingle(c => c.Name == "C1" && c.IsRoot);
    }

    [Fact]
    public async Task Detail_Uninvited_User_And_Missing_Id_Are_Indistinguishable_NotFound()
    {
        var (habit, _, _) = await SeedInvitedAsync();

        // Control: the invitee reads it fine.
        (await _controller.GetById(habit.Id, CancellationToken.None)).Value.Should().NotBeNull();

        HabitTestData.SetupUserClaims(_controller, Stranger, "Eve");
        var strangerCode = await ExpectCodeAsync(() => _controller.GetById(habit.Id, CancellationToken.None));
        var missingCode = await ExpectCodeAsync(() => _controller.GetById(9999, CancellationToken.None));

        strangerCode.Should().Be(HabitErrorCodes.NotFound);
        missingCode.Should().Be(strangerCode); // existence never revealed to the uninvited
    }

    [Fact]
    public async Task Owner_Can_Use_The_Viewer_Endpoints_On_Own_Habit()
    {
        var (habit, _, _) = await SeedInvitedAsync();
        HabitTestData.SetupUserClaims(_controller, Owner, OwnerDisplayName);

        var detail = (await _controller.GetById(habit.Id, CancellationToken.None)).Value!;
        // No grant exists for the owner — the name falls back to the token's own "name".
        detail.OwnerName.Should().Be(OwnerDisplayName);
        detail.Habit.Id.Should().Be(habit.Id);
    }

    [Fact]
    public async Task Revoking_The_Grant_Reprivatizes_Instantly()
    {
        var (habit, _, _) = await SeedInvitedAsync();
        (await _controller.GetAll(CancellationToken.None)).Value.Should().ContainSingle();

        await _context.HabitShareGrants.ExecuteDeleteAsync();

        (await _controller.GetAll(CancellationToken.None)).Value.Should().BeEmpty();
        await ExpectCodeAsync(() => _controller.GetById(habit.Id, CancellationToken.None));
        await ExpectCodeAsync(() => _controller.GetHistory(habit.Id, null, null, CancellationToken.None));
    }

    // ── GET /api/SharedHabits/{id}/History ───────────────────────────────────────────

    [Fact]
    public async Task History_Grants_The_Invitee_The_Full_Punch_Granularity()
    {
        var (habit, item, property) = await SeedInvitedAsync();
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, HabitTestData.Today, numValue: 6.0);

        // Explicit single-day range — the default (unbounded) history walks every day
        // of the habit's window, one DayHistoryOut per day.
        var days = (await _controller.GetHistory(
            habit.Id, HabitTestData.Today, HabitTestData.Today, CancellationToken.None)).Value!;

        var day = days.Should().ContainSingle().Subject;
        var punch = day.Punches.Should().ContainSingle().Subject;
        punch.Values.Single().NumValue.Should().Be(6.0); // identical shape the owner sees
    }

    [Fact]
    public async Task History_Uninvited_User_Is_NotFound()
    {
        var (habit, _, _) = await SeedInvitedAsync();
        HabitTestData.SetupUserClaims(_controller, Stranger, "Eve");

        var code = await ExpectCodeAsync(() =>
            _controller.GetHistory(habit.Id, null, null, CancellationToken.None));
        code.Should().Be(HabitErrorCodes.NotFound);
    }

    [Fact]
    public async Task History_Rejects_Inverted_Range()
    {
        var (habit, _, _) = await SeedInvitedAsync();

        var code = await ExpectCodeAsync(() =>
            _controller.GetHistory(habit.Id, HabitTestData.Today, HabitTestData.Today.AddDays(-1), CancellationToken.None));
        code.Should().Be(HabitErrorCodes.InvalidDateRange);
    }

    [Fact]
    public async Task History_FromMidCycleDay_EvaluatesCumulativeFromCycleStart()
    {
        // A1: the invitee History route shares BuildHistoryAsync — verify the fix reaches
        // it: the Monday punch (8) precedes the from=Wednesday window; the running total
        // for the day must still be 11 ≥ 10. Start is -30d so last week's Monday is
        // always inside the active window.
        var (habit, item, property, _) = await HabitTestData.SeedMinimalRunningAsync(
            _context, Owner, start: HabitTestData.Today.AddDays(-30));
        await HabitTestData.SeedGrantAsync(_context, habit, Viewer, "Bob", OwnerDisplayName);
        var monday = HabitTestData.LastWeekMonday;
        var wednesday = monday.AddDays(2);
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, monday, numValue: 8);
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, wednesday, numValue: 3);

        var result = await _controller.GetHistory(habit.Id, wednesday, wednesday, CancellationToken.None);

        var day = result.Value.Should().ContainSingle().Subject;
        day.CycleFrom.Should().Be(monday);
        day.Criteria[0].CurrentValue.Should().Be(11);
        day.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public async Task Detail_Carries_DeactivatedDate_For_The_Viewer()
    {
        // A-L8: the shared read-only HabitOut mirrors the same DeactivatedDate field.
        var deactivatedOn = HabitTestData.Today.AddDays(-2);
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(
            _context, Owner, state: HabitState.Inactive, deactivatedDate: deactivatedOn);
        await HabitTestData.SeedGrantAsync(_context, habit, Viewer, "Bob", OwnerDisplayName);

        var detail = (await _controller.GetById(habit.Id, CancellationToken.None)).Value!;
        detail.Habit.DeactivatedDate.Should().Be(deactivatedOn);
    }

    [Fact]
    public async Task History_Clamps_End_At_Deactivation()
    {
        // A-L4 on the shared surface: no rows on/after the deactivation day.
        var deactivatedOn = HabitTestData.Today.AddDays(-2);
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(
            _context, Owner, start: HabitTestData.Today.AddDays(-30),
            state: HabitState.Inactive, deactivatedDate: deactivatedOn);
        await HabitTestData.SeedGrantAsync(_context, habit, Viewer, "Bob", OwnerDisplayName);

        var days = (await _controller.GetHistory(habit.Id, null, null, CancellationToken.None)).Value!;
        days.Should().NotBeEmpty();
        days.Max(d => d.Date).Should().Be(deactivatedOn.AddDays(-1));
    }
}
