using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace NewfiesOptimizer.Optimizations;

// Only the veto enumeration is replaced. Stock cancellation, transitions, events,
// task slots and profiler markers remain in the original method.
public static class AiTaskStartOptimizations
{
    private static volatile bool enabled;
    private static readonly FieldInfo Filters = AccessTools.Field(typeof(AiTaskManager), "OnExecuteTask");
    public static MethodInfo Target => AccessTools.Method(typeof(AiTaskManager), "ExecuteTask", [typeof(IAiTask), typeof(int)]);
    public static MethodInfo TranspilerMethod => AccessTools.Method(typeof(AiTaskStartOptimizations), nameof(Transpiler));
    public static void Configure(bool value) => enabled = value;
    public static bool ValidateContract(out string reason)
    {
        bool valid = Target?.ReturnType == typeof(void) && Filters?.FieldType == typeof(AiTaskManager.OnExecuteTaskDelegate);
        reason = valid ? "Official task-start veto contract verified." : "Task-start veto contract differs.";
        return valid;
    }
    public static bool Evaluate(IAiTask task, IAiTask previous, AiTaskManager.OnExecuteTaskDelegate filters)
    {
        if (filters is null) throw new NullReferenceException();
        bool allow = true;
        if (enabled)
            foreach (var callback in Delegate.EnumerateInvocationList(filters)) allow &= callback(task, previous);
        else
            foreach (var callback in filters.GetInvocationList().Cast<AiTaskManager.OnExecuteTaskDelegate>()) allow &= callback(task, previous);
        return allow;
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
    {
        var code = source.ToList();
        int field = code.FindIndex(x => Equals(x.operand, Filters));
        if (field < 1 || field + 2 >= code.Count || code[field + 1].opcode != OpCodes.Brfalse_S || code[field + 2].opcode != OpCodes.Ldc_I4_1)
            throw new InvalidOperationException("Unexpected task-start veto branch; retain stock.");
        var destination = (Label)code[field + 1].operand;
        int end = code.FindIndex(field + 2, x => x.labels.Contains(destination));
        int start = field + 2;
        if (end <= start) throw new InvalidOperationException("Task-start veto destination absent; retain stock.");
        var removed = code.GetRange(start, end - start);
        if (end <= start || code[end].opcode != OpCodes.Ldloc_0 || code[end - 1].opcode != OpCodes.Ret ||
            removed.Count(x => x.operand is MethodInfo m && m.Name == nameof(Delegate.GetInvocationList)) != 1 ||
            removed.Count(x => x.blocks.Any(b => b.blockType == ExceptionBlockType.BeginExceptionBlock)) != 1 ||
            removed.Count(x => x.blocks.Any(b => b.blockType == ExceptionBlockType.EndExceptionBlock)) != 1)
            throw new InvalidOperationException("Unexpected task-start veto body; retain stock.");
        var internalLabels = removed.SelectMany(x => x.labels).ToHashSet();
        if (code.Take(start).Concat(code.Skip(end)).Any(x => x.operand is Label l && internalLabels.Contains(l)))
            throw new InvalidOperationException("Unexpected external branch into task-start veto; retain stock.");
        code.RemoveRange(start, end - start);
        code.InsertRange(start, [new(OpCodes.Ldarg_1), new(OpCodes.Ldloc_0), new(OpCodes.Ldarg_0), new(OpCodes.Ldfld, Filters),
            new(OpCodes.Call, AccessTools.Method(typeof(AiTaskStartOptimizations), nameof(Evaluate))), new(OpCodes.Brtrue, destination), new(OpCodes.Ret)]);
        return code;
    }
}
