# GazetteAI hybrid conversation update

This backend supports four conversation paths:

1. Document questions use embeddings and answer only from the uploaded PDF.
2. Language and clarification commands reuse the previous real document question.
3. Greetings, acknowledgements, and casual conversation return a friendly response without PDF retrieval.
4. Questions unrelated to the PDF ask for permission before using general AI knowledge.

All substantive questions follow a document-first rule. Classification cannot bypass PDF retrieval merely because a question is broad. The permission request remains pending through greetings and casual chat, and closes only after the user accepts or declines it.

The general-knowledge path does not claim to browse the live web. Current facts should be verified with a dedicated web-search provider before production use.

Permission handling is deterministic: only explicit yes/no responses accept or decline. Short unclear replies such as `h`, `hm`, or `?` trigger a clarification prompt and do not discard the pending question.

## Build

```powershell
cd "$env:USERPROFILE\Desktop\GazetteAI\backend"
dotnet clean
dotnet build
```

## Run

```powershell
dotnet run `
  --project .\GazetteAI.Api\GazetteAI.Api.csproj `
  --urls http://localhost:5000
```

## Suggested test

1. `What is this document about?`
2. `tamil la sollu`
3. `puriyala`
4. `theliva sollu`
5. `what about you?`
6. `Who is the president of Sri Lanka?`
7. `yes, tamil la sollu`
8. `thanks`

Document answers include PDF sources. Casual and general-knowledge responses return an empty sources array.
