using System.Text;
using System.Threading.RateLimiting;
using Aiq.Gateway.Auth;
using Aiq.Gateway.Configuration;
using Aiq.Gateway.Data;
using Aiq.Gateway.Endpoints;
using Aiq.Gateway.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<IFileUploadGuard, FileUploadGuard>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
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
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("admin",    p => p.RequireRole("admin"));
    options.AddPolicy("reviewer", p => p.RequireRole("admin", "reviewer"));
    options.AddPolicy("uploader", p => p.RequireRole("admin", "uploader"));
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("default", limiter =>
    {
        limiter.PermitLimit = 100;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("upload", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

// ---- Pipeline --------------------------------------------------------------
var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapUploadEndpoints();

app.MapGet("/me", (HttpContext ctx) =>
{
    var user = ctx.User;
    return Results.Ok(new
    {
        sub   = user.FindFirst("sub")?.Value,
        email = user.FindFirst("email")?.Value,
        roles = user.FindAll("role").Select(c => c.Value).ToArray()
    });
})
.RequireAuthorization()
.RequireRateLimiting("default");

app.Run();

public partial class Program;
