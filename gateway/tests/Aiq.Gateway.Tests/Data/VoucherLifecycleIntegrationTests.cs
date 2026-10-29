using System.Security.Claims;
using Aiq.Gateway.Data;
using Aiq.Gateway.Endpoints;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Vouchers;
using Aiq.Gateway.Vouchers.Lifecycle;
using Aiq.Gateway.Vouchers.Validation;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aiq.Gateway.Tests.Data;

/// <summary>
/// Chứng từ flow v2 PR3 against the real schema: the edit lock of submitted /
/// approved / reversed vouchers, PATCH never posting, the transition endpoints
/// and their ACL (ADMIN Department Authority Invariant), soft delete.
/// </summary>
public sealed class VoucherLifecycleIntegrationTests(TaxVerificationDatabase fx) : IClassFixture<TaxVerificationDatabase>
{
    private const string Company = "0312345673";
    private const string Supplier = "0101243150";

    static VoucherLifecycleIntegrationTests()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
    }

    private NpgsqlConnectionFactory Factory => new(fx.ConnectionString!);
    private VoucherRepository Repo => new(Factory);
    private TenantFeatureFlagRepository Flags => new(Factory);
    private VoucherLifecycleRepository Lifecycle => new(Factory);
    private VoucherValidationService Validation => new(Repo, new TenantCompanyProfileRepository(Factory),
        new VoucherValidationRepository(Factory), Flags, Lifecycle, TimeProvider.System, NullLogger<VoucherValidationService>.Instance);

    // ------------------------------------------------------------ edit lock

    [Theory]
    [InlineData("pending_approval")]
    [InlineData("approved")]
    [InlineData("reversed")]
    public async Task A_locked_voucher_refuses_every_edit_path(string state)
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(lifecycle: state);
        var entry = await JournalAsync(id);
        var before = await FingerprintAsync(id);

        Assert.Equal(VoucherLifecycleGuard.ErrorCode, ErrorOf(await VoucherEndpoints.ApplyExtractionAsync(
            id, Request(Unique()), Repo, Validation, Ctx("admin", fx.Admin), CancellationToken.None)));
        Assert.Equal(VoucherLifecycleGuard.ErrorCode, ErrorOf(await VoucherEndpoints.UpdateAsync(
            id, Patch(total: 5m), Repo, Validation, Flags, Ctx("user", fx.User), CancellationToken.None)));
        Assert.Equal(VoucherLifecycleGuard.ErrorCode, ErrorOf(await VoucherEndpoints.ReextractAsync(
            id, new VoucherEndpoints.ReextractRequest("VĂN BẢN DÁN LẠI ĐỂ GHI ĐÈ CHỨNG TỪ ĐANG CHỜ DUYỆT", null), Repo, null!,
            Ctx("admin", fx.Admin), CancellationToken.None)));
        Assert.Equal(VoucherLifecycleGuard.ErrorCode, ErrorOf(await VoucherEndpoints.DecideJournalAsync(
            id, entry, new AcceptJournalRequest(false, "x"), Repo, Validation, Ctx("admin", fx.Admin), CancellationToken.None)));
        Assert.Equal(VoucherLifecycleGuard.ErrorCode, ErrorOf(await VoucherEndpoints.DeleteAsync(
            id, Repo, Flags, Lifecycle, Ctx("admin", fx.Admin), CancellationToken.None)));
        await Assert.ThrowsAsync<VoucherLockedException>(() => Repo.SaveJournalSuggestionsAsync(fx.Tenant, fx.Dept, id,
            new SaveJournalSuggestionsRequest([new SaveJournalSuggestionDto(1, "642", "331", 1m, null, null)], ReplaceExisting: true),
            CancellationToken.None));

        Assert.Equal(before, await FingerprintAsync(id));
    }

    [Fact]
    public async Task The_repository_guard_holds_when_the_lock_lands_after_the_endpoint_check()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(lifecycle: "pending_approval");

        Assert.False(await Repo.ReplaceExtractedAsync(fx.Tenant, fx.User, id, Request(Unique()), CancellationToken.None));
        Assert.False(await Repo.UpdateOcrResultAsync(id, "ocr_complete", "RAW", 1m, null, CancellationToken.None));
        Assert.Null(await Repo.UpdateAsync(fx.Tenant, fx.User, id, Patch(total: 5m), CancellationToken.None));
        Assert.False(await Repo.DeleteAsync(fx.Tenant, fx.User, id, CancellationToken.None));
        Assert.Equal("pending_approval", await LifecycleAsync(id));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("extracted")]
    [InlineData("needs_fix")]
    [InlineData("validated")]
    public async Task Editable_states_still_accept_edits(string state)
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(lifecycle: state);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await VoucherEndpoints.UpdateAsync(
            id, Patch(total: 5m), Repo, Validation, Flags, Ctx("user", fx.User), CancellationToken.None)));
    }

    // ------------------------------------------------------ PATCH never posts

    [Theory]
    [InlineData("verified")]
    [InlineData("journalized")]
    public async Task Patch_cannot_post_a_voucher(string status)
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync();

        var result = await VoucherEndpoints.UpdateAsync(id, Patch(status: status), Repo, Validation, Flags, Ctx("admin", fx.Admin), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal(("extracted", "extracted"), await StatusAndLifecycleAsync(id));
    }

    /// <summary>
    /// Without the v2 flag the ADMIN still records through apply-invoice /
    /// apply-expense, which write status='verified' inside the Gateway (not via
    /// PATCH). The PR3 guards on UpdateAsync must keep that write working.
    /// </summary>
    [Fact]
    public async Task Without_the_flag_the_legacy_recognition_write_still_posts()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync();

        Assert.NotNull(await Repo.UpdateAsync(fx.Tenant, fx.User, id, Patch(status: "verified"), CancellationToken.None));

        Assert.Equal(("verified", "posted"), await StatusAndLifecycleAsync(id));
        var events = await Lifecycle.ListEventsAsync(fx.Tenant, id, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == "state_change"
            && System.Text.Json.JsonDocument.Parse(e.After!).RootElement.GetProperty("lifecycle_state").GetString() == "posted");
    }

    [Fact]
    public async Task With_the_v2_flag_patch_changes_no_status_at_all()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync();
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await VoucherEndpoints.UpdateAsync(
            id, Patch(status: "rejected"), Repo, Validation, Flags, Ctx("admin", fx.Admin), CancellationToken.None)));

        var other = await VoucherAsync();
        await FlagAsync(true);
        try
        {
            var result = await VoucherEndpoints.UpdateAsync(other, Patch(status: "rejected"), Repo, Validation, Flags, Ctx("admin", fx.Admin), CancellationToken.None);
            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
            Assert.Equal(("extracted", "extracted"), await StatusAndLifecycleAsync(other));
        }
        finally
        {
            await FlagAsync(false);
        }
    }

    // ------------------------------------------------ validation → lifecycle

    [Fact]
    public async Task With_the_v2_flag_storing_a_result_moves_the_voucher_by_its_errors()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync();
        await FlagAsync(true);
        try
        {
            await Validation.RecordAsync(fx.Tenant, fx.Dept, id, CancellationToken.None);
            Assert.Equal("validated", await LifecycleAsync(id));

            var patched = await VoucherEndpoints.UpdateAsync(id, Patch(total: 1m), Repo, Validation, Flags, Ctx("user", fx.User), CancellationToken.None);
            Assert.Equal(StatusCodes.Status200OK, StatusOf(patched));
            Assert.Equal("needs_fix", await LifecycleAsync(id));

            await VoucherEndpoints.UpdateAsync(id, Patch(total: 11_000_000m), Repo, Validation, Flags, Ctx("user", fx.User), CancellationToken.None);
            Assert.Equal("validated", await LifecycleAsync(id));

            // The stored snapshot describes the voucher AFTER the move.
            Assert.True(await StoredMatchesAsync(id));
            var events = await Lifecycle.ListEventsAsync(fx.Tenant, id, CancellationToken.None);
            Assert.Equal(["validated", "needs_fix", "validated"],
                events.Where(e => e.Before is not null).Select(e => System.Text.Json.JsonDocument.Parse(e.After!).RootElement.GetProperty("lifecycle_state").GetString()));
            Assert.All(events, e => Assert.Equal("system", e.ActorType));
        }
        finally
        {
            await FlagAsync(false);
        }
    }

    [Fact]
    public async Task Without_the_flag_storing_a_result_leaves_the_lifecycle_alone()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync();

        await Validation.RecordAsync(fx.Tenant, fx.Dept, id, CancellationToken.None);

        Assert.Equal("extracted", await LifecycleAsync(id));
    }

    /// <summary>
    /// Storing a result (submit, after an edit, the extract job) never moves a
    /// voucher outside extracted / needs_fix / validated — neither a passing nor
    /// a failing one. The result itself is still stored.
    /// </summary>
    [Theory]
    [InlineData("draft", false)]
    [InlineData("draft", true)]
    [InlineData("pending_approval", false)]
    [InlineData("pending_approval", true)]
    [InlineData("approved", false)]
    [InlineData("approved", true)]
    [InlineData("posted", false)]
    [InlineData("posted", true)]
    [InlineData("reversed", false)]
    [InlineData("reversed", true)]
    [InlineData("rejected", false)]
    [InlineData("rejected", true)]
    public async Task Validation_never_moves_a_voucher_outside_the_checked_states(string state, bool withErrors)
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(lifecycle: state, total: withErrors ? 1m : 11_000_000m);
        var before = await LifecycleRowAsync(id);
        var eventsBefore = (await Lifecycle.ListEventsAsync(fx.Tenant, id, CancellationToken.None)).Count;
        await FlagAsync(true);
        try
        {
            var report = await Validation.RecordAsync(fx.Tenant, fx.Dept, id, CancellationToken.None);
            Assert.Equal(withErrors, report!.HasErrors);
            await Validation.TryRecordAsync(fx.Tenant, fx.Dept, id, CancellationToken.None);

            Assert.Equal(before, await LifecycleRowAsync(id));
            Assert.Equal(eventsBefore, (await Lifecycle.ListEventsAsync(fx.Tenant, id, CancellationToken.None)).Count);
            Assert.True(await StoredExistsAsync(id));
        }
        finally
        {
            await FlagAsync(false);
        }
    }

    // --------------------------------------------------- transition endpoints

    private enum Step { Submit, Approve, Return, Reject }

    private Task<IResult> Act(Step step, Guid id, string role, Guid user, string? reason = null, Guid? department = null,
        bool agentHeader = false, bool? post = null)
    {
        var ctx = Ctx(role, user, department);
        if (agentHeader) ctx.Request.Headers["X-AIQ-Actor"] = "agent";
        var request = new VoucherTransitionRequest(reason, post);
        var reviews = new Aiq.Gateway.Vouchers.DocumentProcessing.DocumentReviewRepository(Factory);
        return step switch
        {
            Step.Submit => VoucherLifecycleEndpoints.SubmitAsync(id, request, Repo, Lifecycle, Flags, Validation, reviews, ctx, CancellationToken.None),
            Step.Approve => VoucherLifecycleEndpoints.ApproveAsync(id, request, Repo, Lifecycle, Flags, Validation, reviews, ctx, CancellationToken.None),
            Step.Return => VoucherLifecycleEndpoints.ReturnAsync(id, request, Repo, Lifecycle, Flags, Validation, reviews, ctx, CancellationToken.None),
            _ => VoucherLifecycleEndpoints.RejectAsync(id, request, Repo, Lifecycle, Flags, Validation, reviews, ctx, CancellationToken.None),
        };
    }

    private async Task WithFlagAsync(Func<Task> body)
    {
        await FlagAsync(true);
        try { await body(); }
        finally { await FlagAsync(false); }
    }

    [Fact]
    public async Task Transitions_need_the_v2_flag()
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync();

        Assert.Equal(VoucherLifecycleEndpoints.FlowDisabled, ErrorOf(await Act(Step.Submit, id, "user", fx.User)));
        Assert.Equal("extracted", await LifecycleAsync(id));
    }

    [Fact]
    public async Task An_admin_submits_and_approves_their_own_voucher() => await WithFlagAsync(async () =>
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(creator: fx.Admin);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await Act(Step.Submit, id, "admin", fx.Admin)));
        Assert.Equal("pending_approval", await LifecycleAsync(id));
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await Act(Step.Approve, id, "admin", fx.Admin)));

        var row = await PeopleAsync(id);
        Assert.Equal(("approved", "extracted", (Guid?)fx.Admin, (Guid?)fx.Admin), row);
        var userEvents = (await Lifecycle.ListEventsAsync(fx.Tenant, id, CancellationToken.None)).Where(e => e.ActorType == "user").ToList();
        Assert.Equal(2, userEvents.Count);
        Assert.All(userEvents, e => Assert.Equal(fx.Admin, e.ActorId));
    });

    [Fact]
    public async Task A_user_submits_their_own_voucher_but_never_decides() => await WithFlagAsync(async () =>
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(creator: fx.User);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await Act(Step.Submit, id, "user", fx.User)));
        foreach (var step in new[] { Step.Approve, Step.Return, Step.Reject })
        {
            var refused = await Act(step, id, "user", fx.User, reason: "tự duyệt");
            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(refused));
            Assert.Equal(LifecycleErrors.ForbiddenActor, ErrorOf(refused));
        }
        Assert.Equal("pending_approval", await LifecycleAsync(id));
    });

    [Fact]
    public async Task Another_user_cannot_submit_and_nothing_is_written() => await WithFlagAsync(async () =>
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(creator: fx.Admin2);

        var refused = await Act(Step.Submit, id, "user", fx.User);

        Assert.Equal(LifecycleErrors.ForbiddenActor, ErrorOf(refused));
        Assert.Equal("extracted", await LifecycleAsync(id));
        Assert.False(await StoredExistsAsync(id));
    });

    [Fact]
    public async Task Submit_stores_the_validation_and_refuses_a_voucher_with_errors() => await WithFlagAsync(async () =>
    {
        if (fx.ConnectionString is null) return;
        var bad = await VoucherAsync(total: 1m);

        Assert.Equal("validation_failed", ErrorOf(await Act(Step.Submit, bad, "user", fx.User)));
        Assert.Equal("needs_fix", await LifecycleAsync(bad));
        Assert.True(await StoredMatchesAsync(bad));

        var good = await VoucherAsync();
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await Act(Step.Submit, good, "user", fx.User)));
        Assert.True(await StoredExistsAsync(good));
    });

    [Fact]
    public async Task Return_needs_a_reason_unlocks_the_voucher_and_clears_the_submission() => await WithFlagAsync(async () =>
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync();
        await Act(Step.Submit, id, "user", fx.User);

        Assert.Equal(LifecycleErrors.ReasonRequired, ErrorOf(await Act(Step.Return, id, "admin", fx.Admin, reason: " ")));
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await Act(Step.Return, id, "admin", fx.Admin, reason: "thiếu chữ ký")));

        Assert.Equal(("needs_fix", "extracted", (Guid?)null, (Guid?)null), await PeopleAsync(id));
        var returned = (await Lifecycle.ListEventsAsync(fx.Tenant, id, CancellationToken.None))[^1];
        Assert.Equal(("user", (Guid?)fx.Admin, "thiếu chữ ký"), (returned.ActorType, returned.ActorId, returned.Reason));
        // Editable again.
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await VoucherEndpoints.UpdateAsync(
            id, Patch(total: 11_000_000m), Repo, Validation, Flags, Ctx("user", fx.User), CancellationToken.None)));
    });

    [Fact]
    public async Task Rejected_is_final() => await WithFlagAsync(async () =>
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync();
        await Act(Step.Submit, id, "user", fx.User);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await Act(Step.Reject, id, "admin", fx.Admin, reason: "trùng")));
        Assert.Equal(("rejected", "rejected"), await StatusAndLifecycleAsync(id));
        Assert.Equal(LifecycleErrors.InvalidTransition, ErrorOf(await Act(Step.Submit, id, "admin", fx.Admin)));
    });

    [Theory]
    [InlineData("tenant_owner")]
    [InlineData("super_admin")]
    public async Task Tenant_owner_and_super_admin_cannot_transition(string role) => await WithFlagAsync(async () =>
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(lifecycle: "pending_approval");

        Assert.IsType<ForbidHttpResult>(await Act(Step.Approve, id, role, fx.Admin));
        Assert.Equal("pending_approval", await LifecycleAsync(id));
    });

    [Fact]
    public async Task Another_department_gets_not_found() => await WithFlagAsync(async () =>
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(lifecycle: "pending_approval");

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await Act(Step.Approve, id, "admin", fx.Admin, department: fx.OtherDept)));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await VoucherLifecycleEndpoints.EventsAsync(
            id, Repo, Lifecycle, Ctx("admin", fx.Admin, fx.OtherDept), CancellationToken.None)));
    });

    [Fact]
    public async Task The_agent_header_is_refused_even_with_an_admin_token() => await WithFlagAsync(async () =>
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(creator: fx.Admin);

        var refused = await Act(Step.Submit, id, "admin", fx.Admin, agentHeader: true);

        Assert.Equal(LifecycleErrors.ForbiddenActor, ErrorOf(refused));
        Assert.Equal("extracted", await LifecycleAsync(id));
    });

    [Fact]
    public async Task Approve_checks_type_confirmation_tax_and_post_flag() => await WithFlagAsync(async () =>
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(lifecycle: "pending_approval");

        Assert.Equal("post_not_available", ErrorOf(await Act(Step.Approve, id, "admin", fx.Admin, post: true)));

        await ExecAsync("UPDATE vouchers SET tax_verification_status='invalid' WHERE id=@id", new { id });
        Assert.Equal("tax_verification_invalid", ErrorOf(await Act(Step.Approve, id, "admin", fx.Admin)));
        await ExecAsync("UPDATE vouchers SET tax_verification_status=NULL WHERE id=@id", new { id });

        await ExecAsync("""
            UPDATE vouchers SET extracted_fields = '{"voucher_type_classification": {"voucher_type": "invoice_vat_in", "status": "needs_confirmation", "requires_confirmation": true}}'
             WHERE id=@id
            """, new { id });
        Assert.Equal("voucher_type_unconfirmed", ErrorOf(await Act(Step.Approve, id, "admin", fx.Admin)));
        Assert.Equal("pending_approval", await LifecycleAsync(id));
    });

    [Fact]
    public async Task Events_are_readable_by_the_department() => await WithFlagAsync(async () =>
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync();
        await Act(Step.Submit, id, "user", fx.User);

        var result = await VoucherLifecycleEndpoints.EventsAsync(id, Repo, Lifecycle, Ctx("user", fx.User), CancellationToken.None);

        var events = Assert.IsAssignableFrom<IReadOnlyList<VoucherEventRecord>>((result as IValueHttpResult)?.Value);
        Assert.Contains(events, e => e.After!.Contains("pending_approval"));
    });

    // ------------------------------------------------------------ soft delete

    private Task<IResult> Delete(Guid id, Guid? department = null) =>
        VoucherEndpoints.DeleteAsync(id, Repo, Flags, Lifecycle, Ctx("admin", fx.Admin, department), CancellationToken.None);

    [Theory]
    [InlineData("draft")]
    [InlineData("extracted")]
    [InlineData("needs_fix")]
    [InlineData("validated")]
    [InlineData("rejected")]
    public async Task With_the_v2_flag_delete_is_soft_and_hides_the_voucher(string state) => await WithFlagAsync(async () =>
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync(lifecycle: state);

        Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await Delete(id)));

        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        var row = await c.QuerySingleAsync<(DateTime? DeletedAt, Guid? DeletedBy)>("SELECT deleted_at, deleted_by FROM vouchers WHERE id=@id", new { id });
        Assert.NotNull(row.DeletedAt);
        Assert.Equal(fx.Admin, row.DeletedBy);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM voucher_events WHERE voucher_id=@id AND event_type='deleted'", new { id }));
        // Gone for every read path: detail, list, a second delete.
        Assert.Null(await Repo.FindInDepartmentAsync(fx.Tenant, fx.Dept, id, CancellationToken.None));
        Assert.DoesNotContain(await Repo.ListInDepartmentAsync(fx.Tenant, fx.Dept, null, null, null, 500, 0, CancellationToken.None), v => v.Id == id);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await Delete(id)));
    });

    [Fact]
    public async Task A_voucher_posted_once_is_never_deleted_not_even_softly() => await WithFlagAsync(async () =>
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync();
        // Legacy post then unapply: back to extracted, but posted_at stays.
        await ExecAsync("UPDATE vouchers SET status='verified' WHERE id=@id; UPDATE vouchers SET status='extracted' WHERE id=@id", new { id });

        Assert.Equal(VoucherPostingGuard.ErrorCode, ErrorOf(await Delete(id)));
        Assert.False(await Lifecycle.SoftDeleteAsync(fx.Tenant, fx.Dept, id, fx.Admin, null, CancellationToken.None));
        Assert.NotNull(await Repo.FindInDepartmentAsync(fx.Tenant, fx.Dept, id, CancellationToken.None));
    });

    [Fact]
    public async Task Without_the_flag_delete_stays_hard_but_refuses_a_voucher_posted_once()
    {
        if (fx.ConnectionString is null) return;
        var plain = await VoucherAsync();
        Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await Delete(plain)));
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        Assert.False(await c.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM vouchers WHERE id=@plain)", new { plain }));

        var once = await VoucherAsync();
        await ExecAsync("UPDATE vouchers SET status='verified' WHERE id=@once; UPDATE vouchers SET status='extracted' WHERE id=@once", new { once });
        Assert.Equal(VoucherPostingGuard.ErrorCode, ErrorOf(await Delete(once)));
        Assert.False(await Repo.DeleteAsync(fx.Tenant, fx.User, once, CancellationToken.None));
    }

    [Fact]
    public async Task Soft_deleted_vouchers_cannot_be_transitioned() => await WithFlagAsync(async () =>
    {
        if (fx.ConnectionString is null) return;
        var id = await VoucherAsync();
        Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await Delete(id)));

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await Act(Step.Submit, id, "user", fx.User)));
        Assert.False(await Lifecycle.TransitionAsync(fx.Tenant, fx.Dept, id,
            new LifecycleChange("extracted", "validated", "system", null, null, null), CancellationToken.None));
    });

    // --------------------------------------------------------------- helpers

    private async Task<(string Lifecycle, string Status, Guid? SubmittedBy, Guid? ApprovedBy)> PeopleAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.QuerySingleAsync<(string, string, Guid?, Guid?)>(
            "SELECT lifecycle_state, status, submitted_by, approved_by FROM vouchers WHERE id=@id", new { id });
    }

    private async Task<bool> StoredExistsAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM voucher_validations WHERE voucher_id=@id)", new { id });
    }

    private async Task ExecAsync(string sql, object args)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync(sql, args);
    }

    private async Task<bool> StoredMatchesAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM voucher_validations vv JOIN vouchers v ON v.id = vv.voucher_id
                            WHERE vv.voucher_id = @id AND vv.voucher_updated_at = v.updated_at)
            """, new { id });
    }

    private DefaultHttpContext Ctx(string role, Guid user, Guid? department = null)
    {
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.ToString())], "test")) };
        ctx.Items[AuthenticatedPrincipalContext.ItemKey] = new PrincipalContext(user, fx.Tenant, department ?? fx.Dept, role, true, true, true);
        ctx.Items[TenantContext.ItemKey] = fx.Tenant;
        return ctx;
    }

    private static int? StatusOf(IResult r) => (r as IStatusCodeHttpResult)?.StatusCode;

    private static string? ErrorOf(IResult r) =>
        (r as IValueHttpResult)?.Value?.GetType().GetProperty("error")?.GetValue((r as IValueHttpResult)!.Value) as string;

    private static string Unique() => Guid.NewGuid().ToString("N")[..10];

    private static CreateVoucherRequest Request(string number) => new(
        VoucherType: "invoice_vat_in", DocumentNumber: number, DocumentSeries: "1C26TAA", DocumentDate: new DateOnly(2026, 9, 15),
        SellerName: "Công ty NCC", SellerMst: Supplier, SellerAddress: null, BuyerName: "Công ty Chúng ta", BuyerMst: Company,
        BuyerAddress: null, PaymentMethod: null, Subtotal: 10_000_000m, VatRate: 10m, VatAmount: 1_000_000m, DiscountAmount: null,
        TotalAmount: 11_000_000m, Currency: "VND", AmountInWords: null, SourceFilePath: null, SourceFileName: null, SourceMimeType: null,
        RawOcrText: "RAW", OcrConfidence: null, ExtractedFields: null, Tags: null, LineItems: null);

    private static UpdateVoucherRequest Patch(decimal? total = null, string? status = null) => new(
        Status: status, DocumentNumber: null, DocumentSeries: null, DocumentDate: null, SellerName: null, SellerMst: null,
        SellerAddress: null, BuyerName: null, BuyerMst: null, BuyerAddress: null, PaymentMethod: null, Subtotal: null,
        VatRate: null, VatAmount: null, DiscountAmount: null, TotalAmount: total, Currency: null, AmountInWords: null,
        ErrorMessage: null, Tags: null);

    /// <summary>An invoice that passes the validator, created by fx.User, then moved to <paramref name="lifecycle"/>.</summary>
    private async Task<Guid> VoucherAsync(string lifecycle = "extracted", Guid? creator = null, decimal total = 11_000_000m)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync("""
            INSERT INTO tenant_company_profiles(tenant_id, legal_name, tax_code) VALUES (@t, 'Công ty Chúng ta', @mst)
            ON CONFLICT (tenant_id) DO UPDATE SET tax_code = EXCLUDED.tax_code
            """, new { t = fx.Tenant, mst = Company });
        var id = await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO vouchers(user_id, tenant_id, department_id, voucher_type, status, document_number, document_series, document_date,
                                 seller_name, seller_mst, buyer_name, buyer_mst, subtotal, vat_rate, vat_amount, total_amount, raw_ocr_text)
            VALUES (@u, @t, @d, 'invoice_vat_in', 'extracted', @n, '1C26TAA', '2026-09-15', 'Công ty NCC', @supplier, 'Công ty Chúng ta', @company,
                    10000000, 10, 1000000, @total, 'RAW') RETURNING id
            """, new { u = creator ?? fx.User, t = fx.Tenant, d = fx.Dept, n = Unique(), supplier = Supplier, company = Company, total });
        if (lifecycle != "extracted")
            await c.ExecuteAsync("UPDATE vouchers SET lifecycle_state=@lifecycle WHERE id=@id", new { id, lifecycle });
        return id;
    }

    private async Task<Guid> JournalAsync(Guid voucherId)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO voucher_journal_suggestions(voucher_id, entry_no, debit_account, credit_account, amount)
            VALUES (@voucherId, 1, '1561', '331', 10000000) RETURNING id
            """, new { voucherId });
    }

    private async Task FlagAsync(bool enabled)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync("""
            INSERT INTO tenant_feature_flags(tenant_id, flag, enabled) VALUES (@t, 'voucher_flow_v2', @enabled)
            ON CONFLICT (tenant_id, flag) DO UPDATE SET enabled = EXCLUDED.enabled
            """, new { t = fx.Tenant, enabled });
    }

    /// <summary>Everything an edit could touch: the voucher row (sans timestamps), line items, journal.</summary>
    private async Task<string> FingerprintAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.ExecuteScalarAsync<string>("""
            SELECT md5((to_jsonb(v) - 'updated_at')::text
                       || COALESCE((SELECT jsonb_agg(to_jsonb(l) ORDER BY l.ordinal)::text FROM voucher_line_items l WHERE l.voucher_id = v.id), '')
                       || COALESCE((SELECT jsonb_agg(to_jsonb(j) ORDER BY j.entry_no)::text FROM voucher_journal_suggestions j WHERE j.voucher_id = v.id), ''))
              FROM vouchers v WHERE v.id = @id
            """, new { id }) ?? "";
    }

    private async Task<string> LifecycleAsync(Guid id) => (await StatusAndLifecycleAsync(id)).Lifecycle;

    private async Task<(string Status, string Lifecycle, int Version)> LifecycleRowAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.QuerySingleAsync<(string, string, int)>(
            "SELECT status, lifecycle_state, lifecycle_version FROM vouchers WHERE id=@id", new { id });
    }

    private async Task<(string Status, string Lifecycle)> StatusAndLifecycleAsync(Guid id)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        return await c.QuerySingleAsync<(string, string)>("SELECT status, lifecycle_state FROM vouchers WHERE id=@id", new { id });
    }
}
