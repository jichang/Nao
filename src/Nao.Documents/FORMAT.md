# Nao document format (`.naodoc`)

## Status and scope

This specification describes the current `nao-doc/1` representation implemented
by [NaoDoc](src/Formats/NaoDoc.fs) and the [document model](src/Model/Document.fs).
It specifies portable document content, not an editor, collaboration protocol, or
pixel-identical rendering across output formats.

Nao is pre-1.0; backward compatibility is not required. The schema marker changes
when the format contract changes, independently of NuGet versions and release
tags. Publishing an unchanged format does not change its schema marker.

## Identification and encoding

| Property | Value |
|---|---|
| File extension | `.naodoc` |
| Media type | `application/vnd.nao.document` |
| Root `Schema` value | `nao-doc/1` |
| JSON Schema identifier | `urn:nao:document:1` |
| JSON Schema dialect | JSON Schema Draft 2020-12 |
| Encoding | UTF-8 JSON |

A file contains one JSON document object. It is **not a ZIP archive**. There is no
magic header, embedded binary-resource section, or collaboration-history section.
The standard writer emits compact JSON without a UTF-8 BOM. Whitespace and object
property order are not significant; list order is significant. Serialized bytes
are not a canonical hashing or signing representation.

## JSON value conventions

Property names and union case names are case-sensitive and use the exact spelling
shown here. Writers emit all record fields, including optional fields whose value
is `null`.

| Model value | JSON representation |
|---|---|
| Record | Object with named properties |
| List | Array, including `[]` for an empty list |
| String-keyed map | Object, including `{}` for an empty map |
| Optional value | The underlying value, or `null` |
| Date/time | ISO 8601 date-time string with offset, as emitted for `DateTimeOffset` |
| `ResourceId` | String, not an object |
| `Color` | String, conventionally `#RRGGBB` or `#RRGGBBAA` |
| Other discriminated unions | Tagged objects described below |

Union values have a `Case` string and, when the case has payloads, a `Fields` array.
Payloads occur in declaration order. A payload that is itself a list remains
nested inside `Fields`; it is not flattened.

```json
{"Case":"ThematicBreak"}
{"Case":"Heading","Fields":[1,[]]}
{"Case":"Fluid","Fields":[[]]}
{"Case":"Bold"}
```

In particular, enum-like model unions such as `FontWeight`, `Unit`, and
`PageOrientation` are tagged objects, **not plain strings**. The single-case,
single-field wrappers `ResourceId` and `Color` are unwrapped to strings.

The tables below use `T?` for a nullable value, `T[]` for a list, and
`map<string,T>` for a JSON object mapping strings to values. These are descriptive
notations, not literal JSON syntax.

## Root document

All six properties are part of the current writer shape:

| Property | Type | Meaning |
|---|---|---|
| `Schema` | string | Must be `nao-doc/1` |
| `Metadata` | DocumentMetadata | Descriptive metadata |
| `Resources` | Resource[] | Resource descriptors, not binary contents |
| `Body` | Body | Flowing content or fixed pages |
| `Sources` | map<string,DocumentSourceLocation> | Optional provenance entries; empty map when absent |
| `DefaultPage` | PageSetup? | Page setup hint when paginating flowing content |

### Complete minimal example

```json
{
  "Schema": "nao-doc/1",
  "Metadata": {
    "Title": null,
    "Authors": [],
    "Language": null,
    "Created": null,
    "Modified": null,
    "Properties": {}
  },
  "Resources": [],
  "Body": {
    "Case": "Fluid",
    "Fields": [[{
      "Case": "Paragraph",
      "Fields": [[{
        "Case": "Run",
        "Fields": ["Hello, Nao.", {
          "FontFamily": null,
          "FontSize": null,
          "Weight": null,
          "Style": null,
          "Decorations": [],
          "Color": null,
          "Background": null,
          "Features": {}
        }]
      }], null]
    }]]
  },
  "Sources": {},
  "DefaultPage": null
}
```

`DocumentMetadata` has `Title: string?`, `Authors: string[]`, `Language: string?`,
`Created: date-time?`, `Modified: date-time?`, and
`Properties: map<string,string>`. Language is intended to be a BCP-47 tag.

## Body and pages

| Body case | Ordered payloads |
|---|---|
| `Fluid` | Block[] |
| `Paged` | Page[] |

`Fluid` represents reflowable content. `Paged` represents fixed pages or slides.
Each `Page` has `Setup: PageSetup`, `Header: Block[]`, `Footer: Block[]`, and
`Content: Block[]`. Headers and footers belong to each page; they are not implicit
document-wide templates.

`PageSetup` has `Width: Length`, `Height: Length`,
`Orientation: PageOrientation`, and `Margins: Edges`.
`PageOrientation` cases are `Portrait` and `Landscape`, without payloads.
Orientation is a hint, not an instruction to swap the stored dimensions.
Fluid exporters may flatten paged content in header, content, footer order for
each page. Format conversion does not guarantee layout fidelity.

## Content

The recursive content types are defined in [Content.fs](src/Model/Content.fs).

### Inline cases

| Case | Ordered payloads |
|---|---|
| `Run` | string text, TextStyle |
| `LineBreak` | None |
| `Link` | Inline[] children, string href |
| `InlineImage` | ResourceId, string? alt, Size? |
| `InlineCode` | string |
| `InlineRaw` | string format, string data |

### Block cases

| Case | Ordered payloads |
|---|---|
| `Paragraph` | Inline[], TextAlign? |
| `Heading` | integer level, Inline[] |
| `ListBlock` | ListSpec |
| `Table` | TableSpec |
| `CodeBlock` | string? language, string code |
| `Quote` | Block[] |
| `Media` | MediaSpec |
| `Container` | ContainerSpec |
| `ThematicBreak` | None |
| `BlockRaw` | string format, string data |

Heading level 1 is the top level; editing helpers accept levels 1 through 6.
`LineBreak` is an inline break, distinct from a new paragraph.

### Content records

| Record | Properties |
|---|---|
| ListSpec | `Ordered: boolean`, `Start: integer?`, `Items: ListItem[]` |
| ListItem | `Content: Block[]` |
| TableSpec | `Columns: TableColumn[]`, `Header: TableRow?`, `Rows: TableRow[]`, `Style: BoxStyle?` |
| TableColumn | `Width: Length?`, `Align: TextAlign?` |
| TableRow | `Cells: TableCell[]` |
| TableCell | `Content: Block[]`, `ColSpan: integer`, `RowSpan: integer`, `Style: BoxStyle?` |
| MediaSpec | `Resource: ResourceId`, `Alt: string?`, `Caption: Inline[]`, `Size: Size?` |
| ContainerSpec | `Layout: Layout`, `Children: LayoutChild[]`, `Style: BoxStyle?` |
| LayoutChild | `Content: Block`, `Placement: Placement` |

## Geometry, styling, and layout

See [Geometry.fs](src/Model/Geometry.fs), [Styling.fs](src/Model/Styling.fs), and
[Layout.fs](src/Model/Layout.fs) for the corresponding API types.

| Record | Properties |
|---|---|
| Length | `Value: number`, `Unit: Unit` |
| Point | `X: Length`, `Y: Length` |
| Size | `Width: Length`, `Height: Length` |
| Rect | `X: Length`, `Y: Length`, `Width: Length`, `Height: Length` |
| Edges | `Top: Length`, `Right: Length`, `Bottom: Length`, `Left: Length` |
| TextStyle | `FontFamily: string?`, `FontSize: Length?`, `Weight: FontWeight?`, `Style: FontStyleKind?`, `Decorations: TextDecoration[]`, `Color: Color?`, `Background: Color?`, `Features: map<string,string>` |
| Border | `Width: Length`, `Color: Color`, `Style: BorderStyle`, `Radius: Length` |
| BoxStyle | `Background: Color?`, `Border: Border?`, `Padding: Edges?`, `Margin: Edges?`, `Width: Length?`, `Height: Length?`, `MinWidth: Length?`, `MaxWidth: Length?`, `TextAlign: TextAlign?` |
| FlexOptions | `Direction: FlexDirection`, `Wrap: FlexWrap`, `Justify: Align`, `AlignItems: Align`, `Gap: Length` |
| GridOptions | `Columns: Length[]`, `Rows: Length[]`, `ColumnGap: Length`, `RowGap: Length` |
| Placement | `Column: integer?`, `ColumnSpan: integer?`, `Row: integer?`, `RowSpan: integer?`, `Grow: number?`, `Shrink: number?`, `Basis: Length?`, `Order: integer?`, `Rect: Rect?`, `AlignSelf: Align?` |

All cases in the following table have no payloads:

| Type | Cases |
|---|---|
| Unit | `Px`, `Pt`, `Em`, `Percent`, `Fr`, `Auto` |
| Align | `Start`, `Center`, `End`, `Stretch`, `Baseline`, `SpaceBetween`, `SpaceAround`, `SpaceEvenly` |
| FontWeight | `Thin`, `Light`, `Normal`, `Medium`, `SemiBold`, `Bold`, `Black` |
| FontStyleKind | `Upright`, `Italic`, `Oblique` |
| TextDecoration | `Underline`, `LineThrough`, `Overline` |
| TextAlign | `Left`, `Center`, `Right`, `Justify` |
| BorderStyle | `NoBorder`, `Solid`, `Dashed`, `Dotted` |
| FlexDirection | `Row`, `RowReverse`, `Column`, `ColumnReverse` |
| FlexWrap | `NoWrap`, `Wrap`, `WrapReverse` |

`Layout` cases are `Flow` (no payload), `Flex` (FlexOptions), `Grid` (GridOptions),
and `Absolute` (no payload).

`Pt` means 1/72 inch, `Px` means device-independent pixels, `Em` is relative to
font size, and `Percent` is relative to the containing block. `Fr` describes
remaining grid space; `Auto` is content-derived and ignores `Value`.
For example, 72 points is `{"Value":72,"Unit":{"Case":"Pt"}}`.

Nullable style fields inherit or defer to the consumer when unspecified; there
is no prescribed universal font or theme. Placement grid coordinates are
1-based. Placement fields irrelevant to the parent layout are ignored.

## Resources

[Resource](src/Model/Resources.fs) descriptors have these properties:

| Property | Type | Meaning |
|---|---|---|
| `Id` | string | Document-local identifier referenced by content |
| `Kind` | ResourceKind | Tagged case: `Image`, `Video`, `Audio`, `Font`, `Embedded`, or `Binary` |
| `MediaType` | string | IANA media type |
| `LocalPath` | string? | Relative location in host-managed extracted resources |
| `Uri` | string? | External resource location |
| `Sha256` | string? | Lower-case hexadecimal SHA-256 of resource bytes |
| `PixelWidth` | integer? | Intrinsic pixel width |
| `PixelHeight` | integer? | Intrinsic pixel height |
| `DurationMs` | integer? | Duration in milliseconds |
| `Metadata` | map<string,string> | Additional metadata |

The JSON does not embed resource bytes. A `.naodoc` with local resource references
is not necessarily self-contained: the host must retain and transport associated
files separately. This revision does not specify a bundle layout, resource
resolution precedence, or automatic fetching. Resource IDs are not global
document or collaboration identities.

Consumers must constrain path resolution to authorized storage and apply URI,
network, media, and size policies before loading resources. A descriptor is not
permission to access a local file or fetch a remote URL.

## Source provenance

`Sources` maps structural paths to tagged `DocumentSourceLocation` values:

| Case | Single record payload |
|---|---|
| `ExactText` | `ByteOffset: integer`, `ByteLength: integer`, `CharacterOffset: integer`, `CharacterLength: integer`, `LineStart: integer`, `LineEnd: integer`, `Page: integer?` |
| `PackagePath` | `Part: string`, `Path: string`, `Page: integer?` |
| `PageRegion` | `Page: integer`, `Bounds: Rect`, `Origin: PageCoordinateOrigin` |

`PageCoordinateOrigin` has the payload-free case `BottomLeft`; fixed-page region
coordinates are expressed in typographic points. Exact-text byte offsets refer
to the source bytes, while character offsets follow .NET UTF-16 string indexing.
Line numbers are 1-based. These locations describe the imported source, not byte
ranges in the serialized `.naodoc`.

Top-level paths are `body/{index}`, `pages/{page}/header/{index}`,
`pages/{page}/content/{index}`, and `pages/{page}/footer/{index}`. Block indexes
are zero-based; page numbers in these paths are 1-based. Structural paths are
not stable node IDs under editing. Consumers must not treat stale provenance as
an editing anchor.

## Validation and reader behavior

`NaoDoc.JsonSchema` exposes the generated schema as a JSON string; it is not an
on-disk schema file or a network endpoint. It describes record properties,
required fields, union payload shapes, and the root schema constant. Record and
union objects in that schema disallow additional properties.

Current runtime behavior is deliberately distinguished from schema conformance:

- `deserialize`, `deserializeBytes`, and `Reader.Read` deserialize only. They do
  **not** automatically enforce the root schema marker or call model validation.
- `serialize`, `serializeBytes`, and `Writer.Write` likewise do not validate first.
- `validate` checks the schema marker, duplicate resource IDs, and negative width
  or height in explicitly paged page setups.
- `validateJson` deserializes and runs those model checks, returning diagnostic
  strings. It does **not** execute the generated JSON Schema.
- Default deserialization ignores unknown JSON properties; they are not retained
  for a later write. Unknown union case names cannot be decoded.

Producers should emit the complete schema-conformant shape. Consumers needing
strict structural validation must apply the generated JSON Schema separately,
then apply model and application checks. Callers must reject an unsupported
`Schema` before interpreting, editing, or rewriting a document.

Current model validation is not exhaustive: it does not establish resource
reference integrity, safe URI/path access, table consistency, valid colors,
heading bounds, or all layout constraints. Hosts must apply the additional
checks needed by their renderer/editor and bound input size, nesting, and
resource consumption.

## Extensions and collaboration boundary

Explicit extension points are metadata/property maps, text `Features`, resource
`Metadata`, and the `InlineRaw` / `BlockRaw` format-tagged payloads. Raw payloads
are data, not permission to execute code or inject active HTML. Consumers should
preserve supported raw data and display unsupported content safely rather than
silently converting it to editable text.

This revision defines no stable content-node IDs, edit-operation protocol,
Automerge state, CRDT actor IDs, change heads, cursors, permissions, or presence.
An editor must not infer those contracts from source paths or resource IDs.
Future collaboration state or packaging additions require an explicit design
and, where the format contract changes, a separately assessed schema revision.

## Examples and implementation references

- [Rich fixture](../../tests/Nao.Documents.Tests/Fixtures/rich-sample.naodoc)
- [Native-format tests](../../tests/Nao.Documents.Tests/src/ConversionTests.fs)
- [Editing helpers](src/Editing/DocumentEditor.fs)
- [Durable-format policy](../../docs/durable-formats.md)
