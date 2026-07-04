namespace Nao.Assistant

open Avalonia.Controls
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Layout
open Avalonia.Media

/// Business component: the modal dialog that lets the user attach an optional comment
/// to a 👍 / 👎 rating before submitting feedback on an assistant answer. Pure view —
/// all state and actions are supplied by the caller so it stays Elmish-friendly.
module FeedbackDialog =

    /// Everything needed to render the feedback modal.
    type Props =
        { /// "positive" or "negative" — drives the header and prompt copy.
          Sentiment: string
          /// Current comment text.
          Comment: string
          /// True while the submission is in flight (disables the controls).
          Submitting: bool
          OnCommentChanged: string -> unit
          OnCancel: unit -> unit
          OnSubmit: unit -> unit }

    let render (props: Props) : IView =
        let t = Localization.current ()
        let isPositive = props.Sentiment = "positive"
        let header =
            if isPositive then sprintf "\U0001F44D %s" t.FeedbackPositiveHeader
            else sprintf "\U0001F44E %s" t.FeedbackNegativeHeader
        let prompt =
            if isPositive then t.FeedbackPositivePrompt
            else t.FeedbackNegativePrompt
        Border.create [
            Border.background (SolidColorBrush(Color.Parse("#B3000000")))
            Border.child (
                Border.create [
                    Border.width 440.0
                    Border.padding (20.0, 18.0)
                    Border.cornerRadius 14.0
                    Border.horizontalAlignment HorizontalAlignment.Center
                    Border.verticalAlignment VerticalAlignment.Center
                    Border.background Theme.surfaceRaised
                    Border.borderThickness 1.0
                    Border.borderBrush Theme.border
                    Border.child (
                        StackPanel.create [
                            StackPanel.spacing 12.0
                            StackPanel.children [
                                TextBlock.create [
                                    TextBlock.text header
                                    TextBlock.fontSize 12.0
                                    TextBlock.foreground Theme.textSecondary
                                ]
                                TextBlock.create [
                                    TextBlock.text prompt
                                    TextBlock.fontSize 16.0
                                    TextBlock.fontWeight FontWeight.SemiBold
                                    TextBlock.foreground Theme.textPrimary
                                ]
                                TextBlock.create [
                                    TextBlock.text t.FeedbackCommentHint
                                    TextBlock.fontSize 11.0
                                    TextBlock.foreground Theme.textMuted
                                ]
                                TextBox.create [
                                    TextBox.text props.Comment
                                    TextBox.placeHolderText t.FeedbackCommentPlaceholder
                                    TextBox.acceptsReturn true
                                    TextBox.textWrapping TextWrapping.Wrap
                                    TextBox.height 96.0
                                    TextBox.isEnabled (not props.Submitting)
                                    TextBox.onTextChanged (fun t -> props.OnCommentChanged t)
                                ]
                                StackPanel.create [
                                    StackPanel.orientation Orientation.Horizontal
                                    StackPanel.horizontalAlignment HorizontalAlignment.Right
                                    StackPanel.spacing 8.0
                                    StackPanel.children [
                                        Button.create [
                                            Button.content t.Cancel
                                            Button.isEnabled (not props.Submitting)
                                            Button.onClick (fun _ -> props.OnCancel ())
                                        ]
                                        Button.create [
                                            Button.content (if props.Submitting then t.Sending else t.Submit)
                                            Button.isEnabled (not props.Submitting)
                                            Button.onClick (fun _ -> props.OnSubmit ())
                                        ]
                                    ]
                                ]
                            ]
                        ]
                    )
                ]
            )
        ]
