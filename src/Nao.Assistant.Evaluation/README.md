# Nao.Assistant.Evaluation

Standard evaluation application for measuring Nao.Server behavior with `Nao.Eval`.

The app starts the embedded `Nao.Server`, creates a document-agent session through the server HTTP API, sends a WebSocket chat turn with an attached markdown file, and verifies that generated `.html` and `.pdf` files appear in the session file list.

Run it with a local Ollama model:

```bash
./scripts/start-local-llm.sh qwen2.5:3b
NAO_LLM_PROVIDER=Ollama \
NAO_LLM_ENDPOINT=http://localhost:11434 \
NAO_LLM_MODEL=qwen2.5:3b \
dotnet run --project src/Nao.Assistant.Evaluation/Nao.Assistant.Evaluation.fsproj
```

Reports are written to `artifacts/assistant-evaluation/<timestamp>/report.md` and `report.json` by default. The embedded server's data directory is placed beside those reports at `server-data/`, so conversations, uploads, generated files and task state stay with the evaluation artifacts. Reports include the server agent's response plus a generated-file table/list with each output file name, display name, media type, byte size and artifact-local path. Override the output directory with `NAO_EVAL_OUTPUT_DIR`.