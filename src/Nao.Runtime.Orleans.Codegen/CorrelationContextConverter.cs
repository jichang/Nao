using Nao.Agents;
using Microsoft.FSharp.Core;
using Orleans;
using Orleans.Serialization.Codecs;

namespace Nao.Runtime.Orleans.Serialization;

[GenerateSerializer]
internal struct CorrelationContextSurrogate
{
    [Id(0)]
    public Guid ExecutionId;

    [Id(1)]
    public Guid CorrelationId;

    [Id(2)]
    public bool HasCausationId;

    [Id(3)]
    public Guid CausationId;

    [Id(4)]
    public int Attempt;
}

[RegisterConverter]
internal sealed class CorrelationContextConverter
    : IConverter<CorrelationContext, CorrelationContextSurrogate>
{
    public CorrelationContextSurrogate ConvertToSurrogate(in CorrelationContext value)
    {
        var causationId = value.CausationId;

        return new CorrelationContextSurrogate
        {
            ExecutionId = ExecutionIdModule.value(value.ExecutionId),
            CorrelationId = CorrelationIdModule.value(value.CorrelationId),
            HasCausationId = causationId is not null,
            CausationId = causationId is null ? Guid.Empty : ExecutionIdModule.value(causationId.Value),
            Attempt = value.Attempt,
        };
    }

    public CorrelationContext ConvertFromSurrogate(in CorrelationContextSurrogate surrogate)
    {
        FSharpOption<ExecutionId>? causationId = null;

        if (surrogate.HasCausationId)
        {
            causationId = FSharpOption<ExecutionId>.Some(ExecutionIdModule.ofGuid(surrogate.CausationId));
        }

        return new CorrelationContext(
            ExecutionIdModule.ofGuid(surrogate.ExecutionId),
            CorrelationIdModule.parse(surrogate.CorrelationId.ToString("D")),
            causationId,
            surrogate.Attempt);
    }
}