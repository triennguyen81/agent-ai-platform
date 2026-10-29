namespace Aiq.Gateway.Vouchers.Lifecycle;

/// <summary><c>vouchers.lifecycle_state</c> values (docs/flows/chung-tu-proposed.md §2.1).</summary>
public static class LifecycleStates
{
    public const string Draft = "draft";
    public const string Extracted = "extracted";
    public const string NeedsFix = "needs_fix";
    public const string Validated = "validated";
    public const string PendingApproval = "pending_approval";
    public const string Approved = "approved";
    public const string Posted = "posted";
    public const string Reversed = "reversed";
    public const string Rejected = "rejected";
}

public enum LifecycleAction { ExtractionCompleted, ValidationPassed, ValidationFailed, Submit, Approve, Return, Reject, Post, Reverse }

/// <summary>
/// Who acts. <c>Admin</c> / <c>User</c> are same-department principals (the
/// endpoint has already scoped them); TENANT_OWNER and SUPER_ADMIN have no
/// operational accounting authority; the Agent never moves a voucher (§6.3).
/// </summary>
public enum LifecycleActorKind { System, Agent, User, Admin, TenantOwner, SuperAdmin }

public sealed record LifecycleActor(LifecycleActorKind Kind, bool IsCreator);

public sealed record LifecycleDecision(bool Allowed, string? Target, string? ErrorCode, string? Message)
{
    internal static LifecycleDecision To(string target) => new(true, target, null, null);
    internal static LifecycleDecision Deny(string code, string message) => new(false, null, code, message);
}

public static class LifecycleErrors
{
    public const string InvalidTransition = "invalid_transition";
    public const string ForbiddenActor = "forbidden_actor";
    public const string ReasonRequired = "reason_required";
}

/// <summary>
/// The voucher lifecycle as a pure function: no I/O, no clock. Callers apply
/// the target state atomically (UPDATE … WHERE lifecycle_state = from).
/// ADMIN Department Authority Invariant (CLAUDE.md): a same-department ADMIN
/// may do every step, including on a voucher they created or submitted —
/// there is deliberately no separation-of-duties check here.
/// </summary>
public static class VoucherStateMachine
{
    private static readonly Dictionary<LifecycleAction, (string[] From, string To)> Transitions = new()
    {
        [LifecycleAction.ExtractionCompleted] = ([LifecycleStates.Draft, LifecycleStates.Extracted, LifecycleStates.NeedsFix, LifecycleStates.Validated], LifecycleStates.Extracted),
        [LifecycleAction.ValidationPassed] = ([LifecycleStates.Extracted, LifecycleStates.NeedsFix, LifecycleStates.Validated], LifecycleStates.Validated),
        [LifecycleAction.ValidationFailed] = ([LifecycleStates.Extracted, LifecycleStates.NeedsFix, LifecycleStates.Validated], LifecycleStates.NeedsFix),
        [LifecycleAction.Submit] = ([LifecycleStates.Validated], LifecycleStates.PendingApproval),
        [LifecycleAction.Approve] = ([LifecycleStates.PendingApproval], LifecycleStates.Approved),
        [LifecycleAction.Return] = ([LifecycleStates.PendingApproval, LifecycleStates.Approved], LifecycleStates.NeedsFix),
        [LifecycleAction.Reject] = ([LifecycleStates.Draft, LifecycleStates.Extracted, LifecycleStates.NeedsFix, LifecycleStates.Validated, LifecycleStates.PendingApproval], LifecycleStates.Rejected),
        [LifecycleAction.Post] = ([LifecycleStates.Approved], LifecycleStates.Posted),
        [LifecycleAction.Reverse] = ([LifecycleStates.Posted], LifecycleStates.Reversed),
    };

    private static readonly HashSet<string> Editable =
        [LifecycleStates.Draft, LifecycleStates.Extracted, LifecycleStates.NeedsFix, LifecycleStates.Validated];

    public static LifecycleDecision Decide(string from, LifecycleAction action, LifecycleActor actor, string? reason)
    {
        if (!MayAct(action, actor))
            return LifecycleDecision.Deny(LifecycleErrors.ForbiddenActor, ActorMessage(action, actor));

        var (sources, target) = Transitions[action];
        if (!sources.Contains(from))
            return LifecycleDecision.Deny(LifecycleErrors.InvalidTransition, $"Không thể {Label(action)} chứng từ ở trạng thái '{from}'.");

        if ((action is LifecycleAction.Return or LifecycleAction.Reject or LifecycleAction.Reverse) && string.IsNullOrWhiteSpace(reason))
            return LifecycleDecision.Deny(LifecycleErrors.ReasonRequired, $"Cần nhập lý do để {Label(action)}.");

        return LifecycleDecision.To(target);
    }

    public static bool IsTerminal(string state) => state is LifecycleStates.Reversed or LifecycleStates.Rejected;

    /// <summary>Fields, re-extraction and journal suggestions may change only here (§2.2).</summary>
    public static bool IsEditable(string state) => Editable.Contains(state);

    /// <summary>Soft delete: editable or rejected, and never once posted — not even softly (QĐ 3).</summary>
    public static bool IsDeletable(string state, bool everPosted) =>
        !everPosted && (Editable.Contains(state) || state == LifecycleStates.Rejected);

    private static bool MayAct(LifecycleAction action, LifecycleActor actor) => action switch
    {
        LifecycleAction.ExtractionCompleted or LifecycleAction.ValidationPassed or LifecycleAction.ValidationFailed
            => actor.Kind == LifecycleActorKind.System,
        LifecycleAction.Submit => actor.Kind == LifecycleActorKind.Admin || (actor.Kind == LifecycleActorKind.User && actor.IsCreator),
        _ => actor.Kind == LifecycleActorKind.Admin,
    };

    private static string ActorMessage(LifecycleAction action, LifecycleActor actor) => actor.Kind switch
    {
        LifecycleActorKind.Agent => "Agent không được chuyển trạng thái chứng từ — người dùng phải tự thao tác.",
        LifecycleActorKind.TenantOwner or LifecycleActorKind.SuperAdmin => "Chủ tenant / quản trị hệ thống không duyệt nghiệp vụ kế toán.",
        LifecycleActorKind.User when action == LifecycleAction.Submit => "Chỉ người tạo chứng từ hoặc ADMIN phòng ban được gửi duyệt.",
        LifecycleActorKind.User => $"Chỉ ADMIN phòng ban được {Label(action)}.",
        _ => $"Không được {Label(action)} chứng từ.",
    };

    private static string Label(LifecycleAction action) => action switch
    {
        LifecycleAction.ExtractionCompleted => "ghi kết quả trích xuất cho",
        LifecycleAction.ValidationPassed or LifecycleAction.ValidationFailed => "cập nhật kết quả kiểm tra cho",
        LifecycleAction.Submit => "gửi duyệt",
        LifecycleAction.Approve => "duyệt",
        LifecycleAction.Return => "trả lại",
        LifecycleAction.Reject => "loại",
        LifecycleAction.Post => "ghi sổ",
        LifecycleAction.Reverse => "đảo",
        _ => action.ToString(),
    };
}
