namespace R007.Pos.Core;

/// <summary>An error whose message is written for the person at the till, so it is always safe and useful to show as it is.</summary>
public sealed class OperatorException(string message) : Exception(message);
