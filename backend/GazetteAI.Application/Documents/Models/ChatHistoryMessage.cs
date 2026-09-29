using System;
using System.Collections.Generic;
using System.Text;

namespace GazetteAI.Application.Documents.Models;

public sealed record ChatHistoryMessage(
    string Role,
    string Content);