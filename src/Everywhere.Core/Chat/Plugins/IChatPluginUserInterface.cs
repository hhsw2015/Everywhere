using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Everywhere.Chat.Plugins;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChatPluginTodoStatus
{
    NotStarted,
    InProgress,
    Completed
}

[Serializable]
public sealed class ChatPluginTodoItem
{
    [Description("1-based unique identifier for the todo item.")]
    public required int Id { get; set; }

    [Description("Concise action-oriented todo label displayed in UI.")]
    public required string Title { get; set; }

    [Description("(Optional) Detailed context, requirements, or implementation notes.")]
    public string? Description { get; set; }

    public ChatPluginTodoStatus Status { get; set; } = ChatPluginTodoStatus.NotStarted;
}

[Serializable]
public sealed class ChatPluginQuestion
{
    [Description("Short identifier for the question. Must be unique so answers can be mapped back to the question")]
    [MaxLength(75)]
    public required string Id { get; set; }

    [Description("The question text to display to the user. Keep it concise, ideally one sentence")]
    [MaxLength(300)]
    public required string Question { get; set; }

    [Description("Allow selecting multiple options when options are provided'")]
    public bool MultiSelect { get; set; }

    [Description("Optional list of selectable answers. If omitted, the question is free text")]
    public IReadOnlyList<ChatPluginQuestionOption>? Options { get; set; }
}

[Serializable]
public sealed class ChatPluginQuestionOption
{
    [Description("Main content for the option")]
    public required string Content { get; set; }

    [Description("Optional additional description for the option, such as implications of selecting it")]
    public string? Description { get; set; } = null;

    [Description("Optional flag to indicate the option is recommended.")]
    public bool Recommended { get; set; } = false;
}

[Serializable]
public sealed record ChatPluginQuestionAnswer(
    IReadOnlyList<string> Selected,
    string? FreeText
);

[Flags]
public enum RequestConsentRememberMasks
{
    AllowOnce = 0x1,
    AllowSession = 0x2,
    AlwaysAllow = 0x4,
    Custom = 0x8,

    All = AllowOnce | AllowSession | AllowSession | AlwaysAllow | Custom
}

public readonly record struct RequestConsentResult(bool IsAccepted, string? Reason)
{
    public static RequestConsentResult Accepted => new(true, null);

    public static RequestConsentResult Denied(string? reason = null) => new(false, reason);

    public static implicit operator bool(RequestConsentResult result) => result.IsAccepted;

    public string FormatReason(string prefix)
    {
        return Reason.IsNullOrWhiteSpace() ? prefix : $"{prefix} Reason: {Reason}";
    }
}

/// <summary>
/// Allows chat plugins to interact with the user interface.
/// </summary>
public interface IChatPluginUserInterface
{
    /// <summary>
    /// Gets a display sink for the plugin to output content to the user interface.
    /// </summary>
    /// <returns></returns>
    IChatPluginDisplaySink DisplaySink { get; }

    /// <summary>
    /// Requests user consent for a permission request.
    /// </summary>
    /// <remarks>
    /// Consent is grouped by plugin.function.id, so multiple calls with the same parameters will only prompt the user once (if they choose to remember their decision).
    /// </remarks>
    /// <param name="id"></param>
    /// <param name="headerKey"></param>
    /// <param name="content"></param>
    /// <param name="rememberMasks"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<RequestConsentResult> RequestConsentAsync(
        string? id,
        IDynamicResourceKey headerKey,
        ChatPluginDisplayBlock? content = null,
        RequestConsentRememberMasks rememberMasks = RequestConsentRememberMasks.All,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ask question and wait for answer.
    /// </summary>
    /// <param name="questions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IReadOnlyList<ChatPluginQuestionAnswer>> AskQuestionAsync(
        IReadOnlyList<ChatPluginQuestion> questions,
        CancellationToken cancellationToken = default);
}