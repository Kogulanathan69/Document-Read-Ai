using System.Text;
using System.Threading.RateLimiting;
using GazetteAI.Api.BackgroundServices;
using GazetteAI.Application.Authentication.Interfaces;
using GazetteAI.Application.Documents.BackgroundProcessing;
using GazetteAI.Application.Documents.Interfaces;
using GazetteAI.Application.Documents.Services;
using GazetteAI.Infrastructure.AI;
using GazetteAI.Infrastructure.Authentication;
using GazetteAI.Infrastructure.Data;
using GazetteAI.Infrastructure.Documents;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Pgvector.EntityFrameworkCore;

var builder =
    WebApplication.CreateBuilder(args);

var connectionString =
    builder.Configuration
        .GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "DefaultConnection is missing.");

/*
 * Database
 */

builder.Services.AddDbContext<AppDbContext>(
    options =>
        options.UseNpgsql(
            connectionString,
            npgsqlOptions =>
                npgsqlOptions.UseVector()));

/*
 * Redis distributed cache
 *
 * Used to reduce repeated database / AI work
 * by keeping selected short-lived data in Redis.
 */

var redisConnection =
    builder.Configuration
        .GetConnectionString("Redis")
    ?? "localhost:6379";

builder.Services.AddStackExchangeRedisCache(
    options =>
    {
        options.Configuration =
            redisConnection;

        options.InstanceName =
            "GazetteAI:";
    });

builder.Services.AddControllers();
builder.Services.AddOpenApi();

/*
 * Document extraction services
 */

builder.Services.AddScoped<
    IOcrTextExtractor,
    TesseractOcrTextExtractor>();

builder.Services.AddScoped<
    IPdfTextExtractor,
    PdfPigTextExtractor>();

builder.Services.AddScoped<
    ITextChunker,
    TextChunker>();

/*
 * Background document processing
 *
 * Queue:
 * One shared queue for the whole API process.
 *
 * Processing service:
 * New scoped instance for each worker scope/job.
 *
 * Worker:
 * Continuously waits for queued document jobs.
 */

builder.Services.AddSingleton<
    IDocumentProcessingQueue,
    DocumentProcessingQueue>();

builder.Services.AddScoped<
    IDocumentProcessingService,
    DocumentProcessingService>();

builder.Services.AddHostedService<
    DocumentProcessingWorker>();

/*
 * Ollama embedding service
 */

builder.Services.Configure<OllamaOptions>(
    builder.Configuration.GetSection(
        OllamaOptions.SectionName));

builder.Services.AddHttpClient<
    IEmbeddingService,
    OllamaEmbeddingService>();

/*
 * Groq document question-answering service
 */

builder.Services.Configure<GroqOptions>(
    builder.Configuration.GetSection(
        GroqOptions.SectionName));

builder.Services.AddHttpClient<
    IChatCompletionService,
    GroqChatCompletionService>();

/*
 * Tavily web search
 *
 * Tavily is used only after the user grants
 * permission to search outside the uploaded document.
 */

builder.Services.Configure<TavilyOptions>(
    builder.Configuration.GetSection(
        TavilyOptions.SectionName));

builder.Services.AddHttpClient<
    IWebSearchService,
    TavilyWebSearchService>();

/*
 * SMTP email service
 */

builder.Services.Configure<SmtpOptions>(
    builder.Configuration.GetSection(
        SmtpOptions.SectionName));

builder.Services.AddScoped<
    IEmailService,
    SmtpEmailService>();

/*
 * Authentication and JWT configuration
 */

builder.Services.Configure<JwtOptions>(
    builder.Configuration.GetSection(
        JwtOptions.SectionName));

var jwtOptions =
    builder.Configuration
        .GetSection(JwtOptions.SectionName)
        .Get<JwtOptions>()
    ?? throw new InvalidOperationException(
        "JWT configuration is missing.");

if (string.IsNullOrWhiteSpace(jwtOptions.Key))
{
    throw new InvalidOperationException(
        "JWT key is missing. Configure Jwt:Key " +
        "using .NET user-secrets.");
}

if (Encoding.UTF8.GetByteCount(
        jwtOptions.Key) < 32)
{
    throw new InvalidOperationException(
        "JWT key must contain at least 32 bytes.");
}

builder.Services.AddScoped<
    IAuthService,
    AuthService>();

builder.Services
    .AddAuthentication(
        JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters =
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,

                ValidIssuer =
                    jwtOptions.Issuer,

                ValidAudience =
                    jwtOptions.Audience,

                IssuerSigningKey =
                    new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(
                            jwtOptions.Key)),

                ClockSkew =
                    TimeSpan.FromMinutes(1)
            };
    });

builder.Services.AddAuthorization();

/*
 * API rate limiting
 *
 * Authenticated users:
 * Each user receives an independent rate-limit bucket.
 *
 * Unauthenticated users:
 * Client IP address is used as the bucket key.
 *
 * Current limit:
 * 60 requests per minute.
 */

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode =
        StatusCodes.Status429TooManyRequests;

    options.AddPolicy(
        "ApiRateLimit",
        httpContext =>
        {
            string partitionKey;

            if (httpContext.User.Identity
                    ?.IsAuthenticated == true)
            {
                partitionKey =
                    httpContext.User.FindFirst(
                        System.Security.Claims
                            .ClaimTypes.NameIdentifier)
                        ?.Value
                    ?? httpContext.User.Identity.Name
                    ?? "authenticated-user";
            }
            else
            {
                partitionKey =
                    httpContext.Connection
                        .RemoteIpAddress
                        ?.ToString()
                    ?? "anonymous";
            }

            return RateLimitPartition
                .GetFixedWindowLimiter(
                    partitionKey,
                    _ =>
                        new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 60,

                            Window =
                                TimeSpan.FromMinutes(1),

                            QueueProcessingOrder =
                                QueueProcessingOrder
                                    .OldestFirst,

                            QueueLimit = 0,

                            AutoReplenishment = true
                        });
        });

    options.OnRejected =
        async (context, cancellationToken) =>
        {
            context.HttpContext.Response.ContentType =
                "application/json";

            await context.HttpContext.Response
                .WriteAsJsonAsync(
                    new
                    {
                        status = 429,
                        message =
                            "Too many requests. " +
                            "Please wait a moment " +
                            "and try again."
                    },
                    cancellationToken);
        };
});

/*
 * Angular frontend CORS
 */

builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "AngularFrontend",
        policy =>
        {
            policy
                .WithOrigins(
                    "http://localhost:4200")
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
});

/*
 * Build application
 */

var app = builder.Build();

/*
 * OpenAPI
 */

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

/*
 * Middleware pipeline
 */

app.UseCors("AngularFrontend");

app.UseHttpsRedirection();

/*
 * Authentication must run before rate limiting
 * so authenticated users can be identified by JWT.
 */

app.UseAuthentication();

app.UseAuthorization();

app.UseRateLimiter();

/*
 * Map API controllers and apply
 * the API rate-limit policy.
 */

app.MapControllers()
    .RequireRateLimiting("ApiRateLimit");

app.Run();