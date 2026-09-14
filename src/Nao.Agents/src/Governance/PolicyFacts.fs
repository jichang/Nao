namespace Nao.Agents

open System

[<RequireQualifiedAccess>]
type DataClassification =
    | Public
    | Internal
    | Confidential
    | Restricted

type PolicyFacts =
    private
        { Classifications: Set<DataClassification>
          Destinations: Set<string>
          Models: Set<string> }

[<RequireQualifiedAccess>]
module PolicyFacts =
    let empty =
        { Classifications = Set.empty
          Destinations = Set.empty
          Models = Set.empty }

    let private destinationOrigin (destination: Uri) =
        if
            not destination.IsAbsoluteUri
            || (destination.Scheme <> Uri.UriSchemeHttp
                && destination.Scheme <> Uri.UriSchemeHttps)
            || String.IsNullOrWhiteSpace destination.Host
            || not (String.IsNullOrEmpty destination.UserInfo)
        then
            invalidArg
                (nameof destination)
                "Policy destinations must be absolute HTTP(S) URIs without user information."

        UriBuilder(
            destination.Scheme.ToLowerInvariant(),
            destination.IdnHost.ToLowerInvariant(),
            (if destination.IsDefaultPort then -1 else destination.Port)
        )
            .Uri.GetLeftPart(UriPartial.Authority)

    let private canonicalModel model =
        ResourceAccess.model model |> ResourceAccess.resource

    let create classifications destinations models =
        { Classifications = Set.ofSeq classifications
          Destinations = destinations |> Seq.map destinationOrigin |> Set.ofSeq
          Models = models |> Seq.map canonicalModel |> Set.ofSeq }

    let combine left right =
        { Classifications = Set.union left.Classifications right.Classifications
          Destinations = Set.union left.Destinations right.Destinations
          Models = Set.union left.Models right.Models }

    let ofResourceAccess resources =
        let destinations =
            resources
            |> Seq.choose (function
                | ResourceAccess.Web(_, destination) -> Some(Uri destination)
                | _ -> None)

        let models =
            resources
            |> Seq.choose (function
                | ResourceAccess.Model model -> Some model
                | _ -> None)

        create Seq.empty destinations models

    let classifications facts = facts.Classifications
    let destinations facts = facts.Destinations
    let models facts = facts.Models

    let internal destinationOriginValue destination = destinationOrigin destination

    let internal canonicalModelValue model = canonicalModel model
