using System.Text;
using GazetteAI.Application.Authentication.Interfaces;
using GazetteAI.Application.Documents.Interfaces;
using GazetteAI.Application.Documents.Services;
using GazetteAI.Infrastructure.AI;
using GazetteAI.Infrastructure.Authentication;
using GazetteAI.Infrastructure.Data;
using GazetteAI.Infrastructure.Documents;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

builder.Services.AddDbContext<AppDbContext>(
    options =>
        options.UseNpgsql(
            connectionString,
            npgsqlOptions =>
                npgsqlOptions.UseVector()));

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
 * Tavily is used only after the user grants permission
 * to search outside the uploaded document.
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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("AngularFrontend");

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
