using System;
using System.Collections.Generic;
using System.Text;

namespace GazetteAI.Infrastructure.AI;

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string BaseUrl { get; set; } =
        "http://localhost:11434";

    public string EmbeddingModel { get; set; } =
        "nomic-embed-text-v2-moe";

    public string ChatModel { get; set; } =
        "qwen2.5:3b";

    public int EmbeddingDimensions { get; set; } = 768;
}