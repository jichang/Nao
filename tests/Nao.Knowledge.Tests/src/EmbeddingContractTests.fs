namespace Nao.Knowledge.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Knowledge

[<TestClass>]
type EmbeddingContractTests() =
    let model = ComponentVersion.create "text-embedding-3-large" "2026-09-01"
    let preprocessing = ComponentVersion.create "knowledge-embedding-input" "1"

    let limits = EmbeddingCapabilityLimits.create 64 32768 8192 131072

    let descriptor () =
        EmbeddingCapabilityDescriptor.create
            model
            3072
            EmbeddingNormalization.UnitLength
            VectorDistanceMetric.Cosine
            preprocessing
            limits

    [<TestMethod>]
    member _.``Embedding capability records complete versioned model metadata``() =
        let value = descriptor ()

        Assert.AreEqual(EmbeddingContractVersion.current, value.ContractVersion)
        Assert.AreEqual(model, value.Model)
        Assert.AreEqual(3072, value.Dimensions)
        Assert.AreEqual(EmbeddingNormalization.UnitLength, value.Normalization)
        Assert.AreEqual(VectorDistanceMetric.Cosine, value.DistanceMetric)
        Assert.AreEqual(preprocessing, value.Preprocessing)
        Assert.AreEqual(64, value.Limits.MaximumBatchSize)
        Assert.AreEqual(32768, value.Limits.MaximumInputCharactersPerItem)
        Assert.AreEqual(8192, value.Limits.MaximumInputTokensPerItem)
        Assert.AreEqual(131072, value.Limits.MaximumInputTokensPerBatch)

        Assert.AreEqual(value.ProfileId, value.ProfileId |> EmbeddingProfileId.value |> EmbeddingProfileId.parse)

    [<TestMethod>]
    member _.``Embedding profile identity changes with compatibility-relevant metadata``() =
        let baseline = descriptor ()

        let changedModel =
            EmbeddingCapabilityDescriptor.create
                (ComponentVersion.create model.ComponentId "2026-10-01")
                baseline.Dimensions
                baseline.Normalization
                baseline.DistanceMetric
                baseline.Preprocessing
                baseline.Limits

        let changedDimensions =
            EmbeddingCapabilityDescriptor.create
                baseline.Model
                1536
                baseline.Normalization
                baseline.DistanceMetric
                baseline.Preprocessing
                baseline.Limits

        let changedNormalization =
            EmbeddingCapabilityDescriptor.create
                baseline.Model
                baseline.Dimensions
                EmbeddingNormalization.None
                baseline.DistanceMetric
                baseline.Preprocessing
                baseline.Limits

        let changedMetric =
            EmbeddingCapabilityDescriptor.create
                baseline.Model
                baseline.Dimensions
                baseline.Normalization
                VectorDistanceMetric.DotProduct
                baseline.Preprocessing
                baseline.Limits

        let changedPreprocessing =
            EmbeddingCapabilityDescriptor.create
                baseline.Model
                baseline.Dimensions
                baseline.Normalization
                baseline.DistanceMetric
                (ComponentVersion.create preprocessing.ComponentId "2")
                baseline.Limits

        let changedLimits =
            EmbeddingCapabilityDescriptor.create
                baseline.Model
                baseline.Dimensions
                baseline.Normalization
                baseline.DistanceMetric
                baseline.Preprocessing
                (EmbeddingCapabilityLimits.create 32 32768 8192 131072)

        [ changedModel
          changedDimensions
          changedNormalization
          changedMetric
          changedPreprocessing
          changedLimits ]
        |> List.iter (fun changed -> Assert.AreNotEqual(baseline.ProfileId, changed.ProfileId))

        Assert.AreEqual(baseline.ProfileId, (descriptor ()).ProfileId)

    [<TestMethod>]
    member _.``Embedding capability rejects unusable dimensions and limits``() =
        Assert.ThrowsExactly<System.ArgumentException>(fun () ->
            EmbeddingCapabilityLimits.create 0 32768 8192 131072 |> ignore)
        |> ignore

        Assert.ThrowsExactly<System.ArgumentException>(fun () ->
            EmbeddingCapabilityLimits.create 64 32768 8192 4096 |> ignore)
        |> ignore

        Assert.ThrowsExactly<System.ArgumentException>(fun () ->
            EmbeddingCapabilityDescriptor.create
                model
                0
                EmbeddingNormalization.UnitLength
                VectorDistanceMetric.Cosine
                preprocessing
                limits
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<System.ArgumentException>(fun () -> EmbeddingContractVersion.parse 2 |> ignore)
        |> ignore

        let forged = { descriptor () with Dimensions = 1536 }

        Assert.ThrowsExactly<System.ArgumentException>(fun () ->
            EmbeddingCapabilityDescriptor.validate forged |> ignore)
        |> ignore
