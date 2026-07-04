namespace Nao.Assistant

open Avalonia.Controls
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Media

/// Low-level, presentation-only atoms shared by the business components in this
/// folder. These are intentionally generic (a button, an input); domain-specific
/// pieces like a chat message or the chat input live in their own component files.
[<RequireQualifiedAccess>]
module Components =

    /// A standard action button (default themed style), e.g. Save / Send / Submit.
    let button (text: string) (isEnabled: bool) (onClick: unit -> unit) : IView =
        Button.create [
            Button.content text
            Button.isEnabled isEnabled
            Button.onClick (fun _ -> onClick ())
        ]

    /// A compact, borderless icon button for inline glyph actions (attach, clear, ...).
    let iconButton (glyph: string) (isEnabled: bool) (onClick: unit -> unit) : IView =
        Button.create [
            Button.content glyph
            Button.isEnabled isEnabled
            Button.padding (6.0, 2.0)
            Button.background Theme.transparent
            Button.foreground Theme.textSecondary
            Button.onClick (fun _ -> onClick ())
        ]

    /// A multi-line, word-wrapping text input using the default themed style.
    let multilineInput
        (value: string)
        (placeholder: string)
        (minHeight: float)
        (isEnabled: bool)
        (onChange: string -> unit)
        : IView =
        TextBox.create [
            TextBox.text value
            TextBox.placeHolderText placeholder
            TextBox.acceptsReturn true
            TextBox.textWrapping TextWrapping.Wrap
            TextBox.minHeight minHeight
            TextBox.isEnabled isEnabled
            TextBox.onTextChanged onChange
        ]
