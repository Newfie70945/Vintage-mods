using System.Reflection;
using BlockLogger.Commands;
using Vintagestory.API.Common;

internal static class RollbackChatChecks
{
    internal static void Run()
    {
        var logger = DispatchProxy.Create<ILogger, ChatParserLogger>();
        var state = (ChatParserLogger)(object)logger;
        const string raw = "/rollback preview player <name|uid> <time>; /rollback execute <token>";
        // The official chat GUI wraps success messages this way and reparses its
        // combined history whenever a new line arrives.
        VtmlParser.Tokenize(logger, "<font color=\"#CCe0cfbb\">" + raw + "</font>");
        if (state.Errors == 0) throw new Exception("Fixture did not reproduce malformed rollback help markup.");
        state.Errors = 0;
        foreach (var diagnostic in new[] {raw, "Missing code <font color=bad><itemstack>{0}", new string('<', 50000), "quantity > maximum & \"missing\"", "</font><hk>invalid</hk>"})
        {
            var clean = RollbackChatText.Encode(diagnostic);
            var tokens = VtmlParser.Tokenize(logger, "<font color=\"#CCe0cfbb\">" + clean + "</font><br/><font color=\"#FFFFFF\">Next normal chat message</font>");
            if (state.Errors != 0 || tokens.Length != 3 || tokens[0] is not VtmlTagToken tag || tag.Name != "font" || tag.ChildElements.Any(t => t is not VtmlTextToken)
                || tokens[2] is not VtmlTagToken next || next.ContentText != "Next normal chat message")
                throw new Exception("Sanitized rollback response corrupted official chat tokenization or subsequent chat.");
        }
        Console.WriteLine("5 official Vintage Story VTML parser cases passed; raw help mismatch reproduced, sanitized diagnostics preserve subsequent chat.");
    }
}

public class ChatParserLogger : DispatchProxy
{
    public int Errors;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "Error") Errors++;
        var type = targetMethod?.ReturnType;
        return type == null || type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
