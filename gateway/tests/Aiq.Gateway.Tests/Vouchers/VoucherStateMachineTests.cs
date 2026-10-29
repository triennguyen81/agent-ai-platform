using Aiq.Gateway.Vouchers.Lifecycle;

namespace Aiq.Gateway.Tests.Vouchers;

/// <summary>
/// Chứng từ flow v2 PR3: the full transition matrix, state × action × actor
/// (docs/flows/chung-tu-proposed.md §2.2, §2.3, §6.3; CLAUDE.md ADMIN
/// Department Authority Invariant). The expected table below is written out
/// by hand, independently of the implementation.
/// </summary>
public sealed class VoucherStateMachineTests
{
    private static readonly string[] States =
    [
        "draft", "extracted", "needs_fix", "validated", "pending_approval", "approved", "posted", "reversed", "rejected",
    ];

    private static readonly LifecycleActor System = new(LifecycleActorKind.System, IsCreator: false);
    private static readonly LifecycleActor Agent = new(LifecycleActorKind.Agent, IsCreator: true);
    private static readonly LifecycleActor Creator = new(LifecycleActorKind.User, IsCreator: true);
    private static readonly LifecycleActor OtherUser = new(LifecycleActorKind.User, IsCreator: false);
    private static readonly LifecycleActor AdminCreator = new(LifecycleActorKind.Admin, IsCreator: true);
    private static readonly LifecycleActor Admin = new(LifecycleActorKind.Admin, IsCreator: false);
    private static readonly LifecycleActor TenantOwner = new(LifecycleActorKind.TenantOwner, IsCreator: false);
    private static readonly LifecycleActor SuperAdmin = new(LifecycleActorKind.SuperAdmin, IsCreator: false);

    private static readonly LifecycleActor[] Actors = [System, Agent, Creator, OtherUser, AdminCreator, Admin, TenantOwner, SuperAdmin];

    // (action, from) → to. Who may do it is in Who() below.
    private static readonly Dictionary<(LifecycleAction, string), string> Allowed = new()
    {
        [(LifecycleAction.ExtractionCompleted, "draft")] = "extracted",
        [(LifecycleAction.ExtractionCompleted, "extracted")] = "extracted",
        [(LifecycleAction.ExtractionCompleted, "needs_fix")] = "extracted",
        [(LifecycleAction.ExtractionCompleted, "validated")] = "extracted",
        [(LifecycleAction.ValidationPassed, "extracted")] = "validated",
        [(LifecycleAction.ValidationPassed, "needs_fix")] = "validated",
        [(LifecycleAction.ValidationPassed, "validated")] = "validated",
        [(LifecycleAction.ValidationFailed, "extracted")] = "needs_fix",
        [(LifecycleAction.ValidationFailed, "needs_fix")] = "needs_fix",
        [(LifecycleAction.ValidationFailed, "validated")] = "needs_fix",
        [(LifecycleAction.Submit, "validated")] = "pending_approval",
        [(LifecycleAction.Approve, "pending_approval")] = "approved",
        [(LifecycleAction.Return, "pending_approval")] = "needs_fix",
        [(LifecycleAction.Return, "approved")] = "needs_fix",
        [(LifecycleAction.Reject, "draft")] = "rejected",
        [(LifecycleAction.Reject, "extracted")] = "rejected",
        [(LifecycleAction.Reject, "needs_fix")] = "rejected",
        [(LifecycleAction.Reject, "validated")] = "rejected",
        [(LifecycleAction.Reject, "pending_approval")] = "rejected",
        [(LifecycleAction.Post, "approved")] = "posted",
        [(LifecycleAction.Reverse, "posted")] = "reversed",
    };

    private static bool Who(LifecycleAction action, LifecycleActor a) => action switch
    {
        LifecycleAction.ExtractionCompleted or LifecycleAction.ValidationPassed or LifecycleAction.ValidationFailed
            => a == System,
        // The creator submits their own voucher; a same-department ADMIN submits any.
        LifecycleAction.Submit => a == Creator || a == AdminCreator || a == Admin,
        // Decisions are ADMIN-only — including on a voucher the ADMIN created and submitted.
        _ => a == AdminCreator || a == Admin,
    };

    private static readonly HashSet<LifecycleAction> NeedsReason = [LifecycleAction.Return, LifecycleAction.Reject, LifecycleAction.Reverse];

    public static TheoryData<string, LifecycleAction, int> Cells()
    {
        var data = new TheoryData<string, LifecycleAction, int>();
        foreach (var s in States)
            foreach (var action in Enum.GetValues<LifecycleAction>())
                for (var i = 0; i < Actors.Length; i++) data.Add(s, action, i);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_of_the_matrix(string from, LifecycleAction action, int actorIndex)
    {
        var actor = Actors[actorIndex];
        var decision = VoucherStateMachine.Decide(from, action, actor, reason: "lý do");

        if (!Who(action, actor))
        {
            Assert.False(decision.Allowed);
            Assert.Equal(LifecycleErrors.ForbiddenActor, decision.ErrorCode);
        }
        else if (Allowed.TryGetValue((action, from), out var to))
        {
            Assert.True(decision.Allowed, decision.Message);
            Assert.Equal(to, decision.Target);
        }
        else
        {
            Assert.False(decision.Allowed);
            Assert.Equal(LifecycleErrors.InvalidTransition, decision.ErrorCode);
        }
    }

    [Theory]
    [InlineData(LifecycleAction.Return, "pending_approval")]
    [InlineData(LifecycleAction.Reject, "validated")]
    [InlineData(LifecycleAction.Reverse, "posted")]
    public void Return_reject_and_reverse_require_a_reason(LifecycleAction action, string from)
    {
        foreach (var reason in new[] { null, "", "   " })
        {
            var decision = VoucherStateMachine.Decide(from, action, Admin, reason);
            Assert.False(decision.Allowed);
            Assert.Equal(LifecycleErrors.ReasonRequired, decision.ErrorCode);
        }
        Assert.True(VoucherStateMachine.Decide(from, action, Admin, "sai số tiền").Allowed);
    }

    [Fact]
    public void Other_actions_do_not_need_a_reason()
    {
        foreach (var ((action, from), _) in Allowed.Where(kv => !NeedsReason.Contains(kv.Key.Item1)))
        {
            var actor = action is LifecycleAction.ExtractionCompleted or LifecycleAction.ValidationPassed or LifecycleAction.ValidationFailed
                ? System : Admin;
            Assert.True(VoucherStateMachine.Decide(from, action, actor, reason: null).Allowed, $"{action} from {from}");
        }
    }

    [Fact]
    public void An_admin_completes_the_whole_workflow_on_their_own_voucher()
    {
        var state = "validated";
        foreach (var action in new[] { LifecycleAction.Submit, LifecycleAction.Approve, LifecycleAction.Post, LifecycleAction.Reverse })
        {
            var decision = VoucherStateMachine.Decide(state, action, AdminCreator, reason: "đảo do sai kỳ");
            Assert.True(decision.Allowed, $"{action} from {state}: {decision.Message}");
            state = decision.Target!;
        }
        Assert.Equal("reversed", state);
    }

    [Theory]
    [InlineData("reversed")]
    [InlineData("rejected")]
    public void Terminal_states_have_no_way_out(string terminal)
    {
        Assert.True(VoucherStateMachine.IsTerminal(terminal));
        foreach (var action in Enum.GetValues<LifecycleAction>())
            foreach (var actor in Actors)
                Assert.False(VoucherStateMachine.Decide(terminal, action, actor, "lý do").Allowed);
    }

    [Theory]
    [InlineData("draft", true)]
    [InlineData("extracted", true)]
    [InlineData("needs_fix", true)]
    [InlineData("validated", true)]
    [InlineData("pending_approval", false)]
    [InlineData("approved", false)]
    [InlineData("posted", false)]
    [InlineData("reversed", false)]
    [InlineData("rejected", false)]
    public void Fields_are_editable_only_before_submission(string state, bool editable)
    {
        Assert.Equal(editable, VoucherStateMachine.IsEditable(state));
    }

    [Theory]
    [InlineData("draft", false, true)]
    [InlineData("extracted", false, true)]
    [InlineData("needs_fix", false, true)]
    [InlineData("validated", false, true)]
    [InlineData("rejected", false, true)]
    [InlineData("pending_approval", false, false)]
    [InlineData("approved", false, false)]
    [InlineData("posted", false, false)]
    [InlineData("reversed", false, false)]
    // Ever posted (legacy unapply moved it back to extracted): never deleted, not even softly.
    [InlineData("extracted", true, false)]
    [InlineData("rejected", true, false)]
    public void Delete_is_refused_once_posted_ever(string state, bool everPosted, bool deletable)
    {
        Assert.Equal(deletable, VoucherStateMachine.IsDeletable(state, everPosted));
    }

    [Fact]
    public void Unknown_state_is_an_invalid_transition()
    {
        var decision = VoucherStateMachine.Decide("journalized", LifecycleAction.Submit, Admin, null);
        Assert.Equal(LifecycleErrors.InvalidTransition, decision.ErrorCode);
    }
}
