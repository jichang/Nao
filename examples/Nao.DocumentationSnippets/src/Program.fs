namespace Nao.DocumentationSnippets

module Program =
    [<EntryPoint>]
    let main _ =
        AgentSnippet.verify ()
        ConfigurationSnippet.validate ()
        AdapterSnippet.verifyAsync().GetAwaiter().GetResult()
        printfn "documentation-snippets: agent, configuration, and adapter snippets succeeded"
        0
