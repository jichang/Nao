# Tested documentation snippets

Supported code snippets live in one executable project rather than being copied into Markdown fences that can drift from the public API.

| Snippet | Demonstrates |
|---|---|
| [`AgentSnippet.fs`](https://github.com/jichang/Nao/blob/main/examples/Nao.DocumentationSnippets/src/AgentSnippet.fs) | Constructing a typed text agent and inspecting its immutable contract |
| [`ConfigurationSnippet.fs`](https://github.com/jichang/Nao/blob/main/examples/Nao.DocumentationSnippets/src/ConfigurationSnippet.fs) | Parsing and validating a schema-v1 platform profile and effective diagnostics |
| [`AdapterSnippet.fs`](https://github.com/jichang/Nao/blob/main/examples/Nao.DocumentationSnippets/src/AdapterSnippet.fs) | Registering, activating, checking, and stopping a compiled adapter |

Run all snippets with:

```bash
dotnet run \
  --project examples/Nao.DocumentationSnippets/Nao.DocumentationSnippets.fsproj \
  --configuration Release
```

The supported solution builds this project, and CI executes it after the Release build. Conceptual guides should link to these compiled sources or to another version-tested example instead of embedding uncompiled F# or C#.

Shell and JSON fences remain documentation for operator commands and data formats; they are validated by their owning command, schema, deployment, or policy tests.
