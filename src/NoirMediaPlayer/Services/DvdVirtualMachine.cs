namespace NoirMediaPlayer.Services;

// Registers belong to our VM, not to a running VLC/Windows DVD session. Unknown player
// parameters stay unknown; a missing language/region/stream value must not become zero.
internal sealed class DvdVmState
{
    public IReadOnlyList<ushort> General { get; }
    public IReadOnlyList<ushort?> System { get; }

    internal DvdVmState(IReadOnlyList<ushort>? general = null, IReadOnlyList<ushort?>? system = null)
    {
        if (general is not null && general.Count != 16 || system is not null && system.Count != 24)
            throw new ArgumentException("DVD VM state requires 16 general and 24 system register slots.");
        General = Array.AsReadOnly(general?.ToArray() ?? new ushort[16]);
        System = Array.AsReadOnly(system?.ToArray() ?? new ushort?[24]);
    }

    internal DvdVmState WithGeneral(int register, ushort value)
    {
        if (register is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(register));
        var values = General.ToArray(); values[register] = value;
        return new(values, System);
    }

    internal DvdVmState WithSystem(int register, ushort value)
    {
        if (register is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(register));
        var values = System.ToArray(); values[register] = value;
        return new(General, values);
    }
}

internal enum DvdVmStatus { Completed, Break, NavigationRequested, CompatibilityRequired, StepLimit }
internal enum DvdVmNavigationKind
{
    SelectButton, TopCell, NextCell, PreviousCell, TopProgram, NextProgram, PreviousProgram,
    TopProgramChain, NextProgramChain, PreviousProgramChain, GoUpProgramChain, TailProgramChain, Resume,
    ProgramChain, Chapter, Program, Cell, Exit, GlobalTitle, TitleSetTitle, TitleSetChapter,
    FirstPlay, ManagerMenu, TitleSetMenu, ManagerProgramChain,
    CallFirstPlay, CallManagerMenu, CallTitleSetMenu, CallManagerProgramChain
}
// Symbolic authored targets only. A domain/IFO/return-stack resolver must validate and
// accept these before a caller commits the proposed state or moves any playback stream.
internal sealed record DvdVmNavigation(DvdVmNavigationKind Kind, int Target = 0, int Button = 0,
    int TitleSet = 0, int Title = 0, int ResumeCell = 0);
internal sealed record DvdVmExecution(DvdVmStatus Status, DvdVmState State, int InstructionsExecuted,
    int Line, DvdVmNavigation? Navigation = null, string? Reason = null);

internal static partial class DvdVirtualMachine
{
    internal static DvdVmExecution Execute(IReadOnlyList<DvdButtonCommand> commands, DvdVmState state,
        int stepLimit = 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands); ArgumentNullException.ThrowIfNull(state);
        if (commands.Count > 255) throw new ArgumentException("A DVD command table is limited to 255 rows.", nameof(commands));
        if (stepLimit is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(stepLimit));
        cancellationToken.ThrowIfCancellationRequested();
        var program = commands.ToArray();
        if (program.Any(command => command is null)) throw new ArgumentException("A command row cannot be null.", nameof(commands));
        var general = state.General.ToArray(); var system = state.System.ToArray();
        var line = 0; var steps = 0;
        DvdVmExecution Success(DvdVmStatus status, DvdVmNavigation? navigation = null) =>
            new(status, new(general, system), steps, line + 1, navigation);
        ushort Register(int code) => code < 16 ? general[code] : system[code - 0x80] ??
            throw new UnsupportedInstruction($"Player system register {code - 0x80} is unknown.");
        ushort Value(Operand operand) => operand.IsRegister ? Register(operand.Value) : operand.Value;
        try
        {
            while (line < program.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (steps == stepLimit) return new(DvdVmStatus.StepLimit, state, steps, line + 1, Reason: "DVD command step limit reached; state was not committed.");
                steps++;
                var instruction = Decode(program[line].Value);
                if (instruction.Condition is { } condition)
                {
                    var left = Register(condition.Left); var right = Value(condition.Right);
                    var matches = condition.Comparison switch { 1 => (left & right) != 0, 2 => left == right,
                        3 => left != right, 4 => left >= right, 5 => left > right, 6 => left <= right, _ => left < right };
                    if (!matches) { line++; continue; }
                }
                switch (instruction.Operation)
                {
                    case Operation.Nop: break;
                    case Operation.Goto:
                        if (instruction.Target < 1 || instruction.Target > program.Length)
                            throw new UnsupportedInstruction("Goto target is outside this command table.");
                        line = instruction.Target - 1; continue;
                    case Operation.Break: return Success(DvdVmStatus.Break);
                    case Operation.Navigate: return Success(DvdVmStatus.NavigationRequested, instruction.Navigation);
                    case Operation.Highlight:
                        var highlight = Value(instruction.Source);
                        if ((highlight & 1023) != 0 || (highlight >> 10) is < 1 or > 36)
                            throw new UnsupportedInstruction("Set-highlight value is not a supported DVD button number.");
                        system[8] = highlight; break;
                    default:
                        var operand = Value(instruction.Source); var current = general[instruction.Target];
                        general[instruction.Target] = instruction.Operation switch
                        {
                            Operation.Assign or Operation.Swap => operand,
                            Operation.Add => (ushort)Math.Min(ushort.MaxValue, current + operand),
                            Operation.Subtract => (ushort)Math.Max(0, current - operand),
                            Operation.Multiply => (ushort)Math.Min(ushort.MaxValue, (long)current * operand),
                            Operation.Divide => operand == 0 ? ushort.MaxValue : (ushort)(current / operand),
                            Operation.Modulo => operand == 0 ? ushort.MaxValue : (ushort)(current % operand),
                            Operation.And => (ushort)(current & operand), Operation.Or => (ushort)(current | operand),
                            Operation.Xor => (ushort)(current ^ operand), _ => throw new UnsupportedInstruction("Unsupported arithmetic operation.")
                        };
                        if (instruction.Operation == Operation.Swap) general[instruction.Source.Value] = current;
                        break;
                }
                line++;
            }
            return Success(DvdVmStatus.Completed);
        }
        catch (UnsupportedInstruction exception)
        {
            return new(DvdVmStatus.CompatibilityRequired, state, steps, line + 1, Reason: exception.Message);
        }
    }

    private enum Operation { Nop, Goto, Break, Assign, Swap, Add, Subtract, Multiply, Divide, Modulo, And, Or, Xor, Highlight, Navigate }
    private readonly record struct Operand(ushort Value, bool IsRegister = false);
    private sealed record Condition(int Comparison, int Left, Operand Right);
    private sealed record Instruction(Operation Operation, int Target = 0, Operand Source = default,
        Condition? Condition = null, DvdVmNavigation? Navigation = null);
    private sealed class UnsupportedInstruction(string message) : Exception(message);
}
