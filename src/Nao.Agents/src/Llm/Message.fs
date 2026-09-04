namespace Nao.Agents

/// A single message in a conversation
type Message = { Role: Role; Content: string }

/// A conversation is a sequence of messages
type Conversation = Message list
