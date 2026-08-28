using System.Text;
using System.Threading.RateLimiting;
using Aiq.Gateway.Audit;
using Aiq.Gateway.Auth;
using Aiq.Gateway.Books;
using Aiq.Gateway.Configuration;
using Aiq.Gateway.Contracts;
using Aiq.Gateway.Data;
using Aiq.Gateway.Endpoints;
using Aiq.Gateway.Governance;
using Aiq.Gateway.Jobs;
using Aiq.Gateway.Middleware;
using Aiq.Gateway.Notes;
using Aiq.Gateway.Observability;
using Aiq.Gateway.Security;
using Aiq.Gateway.Storage;
using Aiq.Gateway.Vouchers;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Console;
using Microsoft.IdentityModel.Tokens;
using Prometheus;

// Npgsql 8 returns `DateTime` (kind=Utc) for `timestamptz` columns. Our records
// historically declared `DateTimeOffset` for those fields; flipping this switch
// lets Npgsql write/read DateTimeOffset against timestamptz transparently.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// Dapper needs a hint to materialize Postgres TEXT[] columns into
// `string[]` record fields — without this the notes/users LIST queries
// throw InvalidOperationException at runtime.
Dapper.SqlMapper.AddTypeHandler(new Aiq.Gateway.Data.StringArrayTypeHandler());
Dapper.SqlMapper.AddTypeHandler(new Aiq.Gateway.Data.DateOnlyTypeHandler());
Dapper.SqlMapper.AddTypeHandler(new Aiq.Gateway.Data.NullableDateOnlyTypeHandler());

var builder = WebApplication.CreateBuilder(args);

// ---- Structured logging ----------------------------------------------------
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.UseUtcTimestamp = true;
    options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions
    {
        Indented = false,
    };
});

// ---- Options ---------------------------------------------------------------
builder.Services
    .AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<FileUploadOptions>()
    .Bind(builder.Configuration.GetSection(FileUploadOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var fileUploadOptions = builder.Configuration
    .GetSection(FileUploadOptions.SectionName)
    .Get<FileUploadOptions>()
    ?? new FileUploadOptions();

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = fileUploadOptions.MaxBytes;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = fileUploadOptions.MaxBytes;
});

builder.Services
    .AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection(AgentOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var agentOptions = builder.Configuration
    .GetSection(AgentOptions.SectionName)
    .Get<AgentOptions>()
    ?? new AgentOptions();

builder.Services
    .AddOptions<JobsOptions>()
    .Bind(builder.Configuration.GetSection(JobsOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var jobsOptions = builder.Configuration
    .GetSection(JobsOptions.SectionName)
    .Get<JobsOptions>()
    ?? new JobsOptions();

var jwtOptions = builder.Configuration
    .GetSection(JwtOptions.SectionName)
    .Get<JwtOptions>()
    ?? throw new InvalidOperationException("Jwt configuration is missing.");

if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey) || jwtOptions.SigningKey.Length < 32)
{
    throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters.");
}

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");

// ---- Services --------------------------------------------------------------
builder.Services.AddSingleton(new NpgsqlConnectionFactory(connectionString));
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddSingleton<DatabaseMigrator>();
builder.Services.AddScoped<IBookRepository, BookRepository>();
builder.Services.AddScoped<INoteRepository, NoteRepository>();
builder.Services.AddScoped<IContractRepository, ContractRepository>();
builder.Services.AddScoped<IChecklistRepository, ChecklistRepository>();
builder.Services.AddScoped<IVoucherRepository, VoucherRepository>();
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<IAgentRepository, AgentRepository>();
builder.Services.AddScoped<IAgentAssignmentRepository, AgentAssignmentRepository>();
builder.Services.AddScoped<IDynamicAgentRepository, DynamicAgentRepository>();
builder.Services.AddScoped<IToolRepository, ToolRepository>();
builder.Services.AddScoped<IWorkflowRepository, WorkflowRepository>();
builder.Services.AddScoped<IReviewBoardRepository, ReviewBoardRepository>();
builder.Services.AddScoped<IHitlRepository, HitlRepository>();
builder.Services.AddScoped<IAgentKnowledgeRepository, AgentKnowledgeRepository>();
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<IFileUploadGuard, FileUploadGuard>();
builder.Services.AddSingleton<IBookAccessControl, BookAccessControl>();

// ---- Object storage (S3-compatible, MinIO on cosmos) ------------------------
// Registered even before we've refactored voucher upload to use it — lets us
// smoke-test connectivity via /healthz/ready and stage the migration behind
// a feature flag (see docs/arch/s3-migration-plan.md).
builder.Services.Configure<S3Options>(builder.Configuration.GetSection(S3Options.SectionName));
builder.Services.AddSingleton<IObjectStorage, S3ObjectStorage>();

builder.Services
    .AddHttpClient<IAgentClient, AgentClient>(client =>
    {
        client.BaseAddress = new Uri(agentOptions.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(agentOptions.TimeoutSeconds);
    });

// ---- Hangfire (background jobs) -------------------------------------------
builder.Services.AddHangfire(config =>
{
    config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(options =>
            options.UseNpgsqlConnection(connectionString));
});

builder.Services.AddHangfireServer(server =>
{
    server.WorkerCount = jobsOptions.WorkerCount;
    server.Queues = ["default"];
});

builder.Services.AddScoped<OcrJobRunner>();
builder.Services.AddScoped<IOcrJobScheduler, OcrJobScheduler>();
builder.Services.AddScoped<ContractDeadlineJob>();

builder.Services.AddScoped<IAuditLogger, PostgresAuditLogger>();

// ---- Observability (OpenTelemetry tracing) --------------------------------
builder.Services
    .AddOptions<ObservabilityOptions>()
    .Bind(builder.Configuration.GetSection(ObservabilityOptions.SectionName));

var observabilityOptions = builder.Configuration
    .GetSection(ObservabilityOptions.SectionName)
    .Get<ObservabilityOptions>()
    ?? new ObservabilityOptions();

builder.Services.AddAiqOpenTelemetry(observabilityOptions);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            NameClaimType = "sub",
            RoleClaimType = "role",
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Role model: super_admin (manages platform + tenants) ⊃ tenant_owner
    // (sole accountable owner of one tenant) ⊃ admin (tenant contributors)
    // ⊃ user (consumers). tenant_owner is folded into the "admin" and "user"
    // policies so an owner never has LESS access than a plain admin — it
    // does not change what any existing super_admin/admin/user can already
    // do. Owner-exclusive actions (create/replace/transfer TENANT_OWNER)
    // stay gated behind the literal "super_admin" policy, never "admin".
    options.AddPolicy("super_admin", p => p.RequireRole("super_admin"));
    options.AddPolicy("tenant_owner", p => p.RequireRole("tenant_owner"));
    options.AddPolicy("admin",       p => p.RequireRole("super_admin", "tenant_owner", "admin"));
    options.AddPolicy("user",        p => p.RequireRole("super_admin", "tenant_owner", "admin", "user"));
    // Legacy policies kept for backward compatibility with older endpoints / data.
    options.AddPolicy("reviewer",    p => p.RequireRole("super_admin", "admin", "reviewer"));
    options.AddPolicy("uploader",    p => p.RequireRole("super_admin", "admin", "uploader"));
    // A role=user session normally can't reach these endpoints. The one
    // exception is a request carrying the "scope=tool_execution" claim —
    // minted only inside GET /runtime/agents/{id}/manifest, short-lived,
    // scoped to the caller's own real role/tenant claims (TenantContext
    // Middleware rejects any token whose role claims don't exactly equal the
    // user's persisted DB roles, so this can never carry an invented role —
    // only this extra claim marks it as tool-execution-authorized). Never
    // handed to the browser/user directly.
    options.AddPolicy("resource_or_tool", p => p.RequireAssertion(ctx =>
        Aiq.Gateway.Governance.ResourceOrToolPolicy.IsAuthorized(
            ctx.User.IsInRole("super_admin"),
            ctx.User.IsInRole("admin") || ctx.User.IsInRole("tenant_owner"),
            ctx.User.IsInRole("user"),
            ctx.User.HasClaim("scope", "tool_execution"))));
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Auth endpoints stay GLOBAL — we cannot key by user until after login.
    options.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });

    // Default + upload are per-user (sub claim) with IP fallback for unauthenticated
    // edge cases. This prevents a single noisy user/key from starving everyone else.
    options.AddPolicy("default", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: PartitionKeyFor(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    options.AddPolicy("upload", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: PartitionKeyFor(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

static string PartitionKeyFor(HttpContext httpContext)
{
    var sub = httpContext.User.FindFirst("sub")?.Value;
    if (!string.IsNullOrWhiteSpace(sub))
    {
        return $"user:{sub}";
    }
    var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return $"ip:{ip}";
}

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

// ---- Performance: response compression + output caching --------------------
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});

builder.Services.AddOutputCache(options =>
{
    // Books listing is hit hard and read-mostly. 10s TTL keeps the cache
    // useful without making a freshly uploaded book invisible for long.
    options.AddPolicy("books-list", policy =>
        policy
            .Expire(TimeSpan.FromSeconds(10))
            .SetVaryByQuery("status", "q", "limit", "offset")
            .Tag("books"));
});

// ---- Pipeline --------------------------------------------------------------
var app = builder.Build();

if (builder.Configuration.GetValue("Database:RunMigrations", true))
{
    await app.Services.GetRequiredService<DatabaseMigrator>()
        .ApplyAsync(CancellationToken.None);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseResponseCompression();
app.UseRouting();
app.UseHttpMetrics();           // /metrics request histograms + counters per endpoint
app.UseRateLimiter();
app.UseOutputCache();
app.UseAuthentication();
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthorization();
app.UseMiddleware<AuditMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapUploadEndpoints();
app.MapBookEndpoints();
app.MapNoteEndpoints();
app.MapContractEndpoints();
app.MapChecklistEndpoints();
app.MapVoucherEndpoints();
app.MapNotificationEndpoints();
app.MapTenantEndpoints();
app.MapAgentEndpoints();
app.MapDynamicAgentEndpoints();
app.MapToolEndpoints();
app.MapWorkflowEndpoints();
app.MapReviewEndpoints();
app.MapHitlEndpoints();
app.MapAgentKnowledgeEndpoints();
app.MapUserEndpoints();
app.MapMetrics();              // Prometheus scrape endpoint at /metrics

if (!string.IsNullOrWhiteSpace(jobsOptions.DashboardPath))
{
    app.UseHangfireDashboard(jobsOptions.DashboardPath, new DashboardOptions
    {
        Authorization =
        [
            new JobsAuthorizationFilter(allowLoopback: app.Environment.IsDevelopment()),
        ],
        IgnoreAntiforgeryToken = false,
    });
}

// Recurring jobs — must run AFTER the Hangfire server is configured.
// Daily 07:00 Asia/Ho_Chi_Minh scan for contract checklist deadlines.
RecurringJob.AddOrUpdate<ContractDeadlineJob>(
    ContractDeadlineJob.RecurringJobId,
    job => job.RunAsync(CancellationToken.None),
    "0 7 * * *",
    new RecurringJobOptions
    {
        TimeZone = TryGetVnTimeZone(),
    });

static TimeZoneInfo TryGetVnTimeZone()
{
    // Linux uses IANA names, Windows uses Windows IDs. Probe both so the
    // job schedules correctly on cosmos (Linux) and dev (Windows).
    try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); }
    catch (TimeZoneNotFoundException) { }
    try { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
    catch (TimeZoneNotFoundException) { }
    return TimeZoneInfo.Utc;
}

app.MapGet("/me", (HttpContext ctx) =>
{
    var user = ctx.User;
    return Results.Ok(new
    {
        sub   = user.FindFirst("sub")?.Value,
        email = user.FindFirst("email")?.Value,
        roles = user.FindAll("role").Select(c => c.Value).ToArray(),
        tenantId = TenantContext.TryGet(ctx, out var tenantId) ? tenantId : (Guid?)null
    });
})
.RequireAuthorization()
.RequireRateLimiting("default");

app.Run();

public partial class Program;
