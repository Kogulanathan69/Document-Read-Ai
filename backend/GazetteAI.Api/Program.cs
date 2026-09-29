using GazetteAI.Application.Documents.Interfaces;
using GazetteAI.Application.Documents.Services;
using GazetteAI.Infrastructure.AI;
using GazetteAI.Infrastructure.Data;
using GazetteAI.Infrastructure.Documents;
using Microsoft.EntityFrameworkCore;
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
            {
                npgsqlOptions.UseVector();

                // Supabase temporary connection failure ???????
                // automatic retry ????????.
                npgsqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay:
                        TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);

                npgsqlOptions.CommandTimeout(60);
            }));

builder.Services.AddControllers();
builder.Services.AddOpenApi();

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
 * Ollama is used only for embeddings.
 */
builder.Services.Configure<OllamaOptions>(
    builder.Configuration.GetSection(
        OllamaOptions.SectionName));

builder.Services.AddHttpClient<
    IEmbeddingService,
    OllamaEmbeddingService>();

/*
 * Groq is used for document question answers.
 */
builder.Services.Configure<GroqOptions>(
    builder.Configuration.GetSection(
        GroqOptions.SectionName));

builder.Services.AddHttpClient<
    IChatCompletionService,
    GroqChatCompletionService>();

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
app.UseAuthorization();

app.MapControllers();

app.Run();