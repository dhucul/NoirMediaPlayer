namespace NoirMediaPlayer.Services;

internal static partial class DvdVirtualMachine
{
    // Decode only the field combinations we implement. Every nonzero reserved/unused
    // bit is rejected, even on an instruction whose condition would be false.
    private sealed class Fields(ulong word)
    {
        private ulong allowed;
        internal int Byte(int index, int mask = 255)
        {
            var shift = (7 - index) * 8; allowed |= (ulong)mask << shift;
            return (int)(word >> shift) & mask;
        }
        internal int Word(int index) => Byte(index) << 8 | Byte(index + 1);
        internal void Finish()
        {
            if ((word & ~allowed) != 0) throw new UnsupportedInstruction("Reserved or unsupported DVD command bits are set.");
        }
    }

    private static Instruction Decode(ulong word)
    {
        var fields = new Fields(word);
        var family = fields.Byte(0, 0xe0) >> 5;
        var comparison = fields.Byte(1, 0x70) >> 4;
        Condition? condition = null;
        if (comparison != 0)
        {
            if (family is 0 || family == 1 && (word & 0x1000000000000000) == 0)
                condition = new(comparison, RegisterCode(fields.Byte(3)), ReadOperand(fields, 4, fields.Byte(1, 0x80) != 0));
            else if (family == 3)
                condition = new(comparison, RegisterCode(fields.Byte(2)), ReadOperand(fields, 6, fields.Byte(1, 0x80) != 0));
            else if (family is 1 or 2)
                condition = new(comparison, RegisterCode(fields.Byte(6)), new((ushort)RegisterCode(fields.Byte(7)), true));
        }
        Instruction instruction;
        switch (family)
        {
            case 0:
                instruction = fields.Byte(1, 15) switch
                {
                    0 => new(Operation.Nop), 1 => new(Operation.Goto, fields.Byte(7)), 2 => new(Operation.Break),
                    _ => throw new UnsupportedInstruction("Parental-level changes or unknown special commands require compatibility handling.")
                };
                break;
            case 1:
                instruction = new(Operation.Navigate, Navigation: fields.Byte(0, 0x10) == 0 ? DecodeLink(fields) : DecodeJump(fields));
                // LinkNoLink with no button is a genuine no-op, not a stream movement.
                if (instruction.Navigation is { Kind: DvdVmNavigationKind.SelectButton, Button: 0 }) instruction = new(Operation.Nop);
                break;
            case 2:
                if (fields.Byte(0, 15) != 6 || fields.Byte(1, 15) != 0)
                    throw new UnsupportedInstruction("Stream, timer, counter-mode and combined system-set commands require compatibility handling.");
                var source = ReadOperand(fields, 4, fields.Byte(0, 0x10) != 0);
                if (source.IsRegister && source.Value >= 16) throw new UnsupportedInstruction("Set-highlight requires a general register source.");
                instruction = new(Operation.Highlight, Source: source);
                break;
            case 3:
                if (fields.Byte(1, 15) != 0) throw new UnsupportedInstruction("Combined arithmetic/link commands require compatibility handling.");
                var operation = fields.Byte(0, 15) switch
                {
                    1 => Operation.Assign, 2 => Operation.Swap, 3 => Operation.Add, 4 => Operation.Subtract,
                    5 => Operation.Multiply, 6 => Operation.Divide, 7 => Operation.Modulo,
                    9 => Operation.And, 10 => Operation.Or, 11 => Operation.Xor,
                    _ => throw new UnsupportedInstruction("Random or unknown general-register operations require compatibility handling.")
                };
                var operand = ReadOperand(fields, 4, fields.Byte(0, 0x10) != 0);
                if (operation == Operation.Swap && (!operand.IsRegister || operand.Value >= 16))
                    throw new UnsupportedInstruction("Swap requires two general registers.");
                instruction = new(operation, fields.Byte(3, 15), operand);
                break;
            default: throw new UnsupportedInstruction("Combined instruction families are not implemented.");
        }
        fields.Finish();
        return instruction with { Condition = condition };
    }

    private static Operand ReadOperand(Fields fields, int index, bool immediate) => immediate
        ? new((ushort)fields.Word(index)) : new((ushort)RegisterCode(fields.Byte(index + 1)), true);

    private static int RegisterCode(int code) => code is >= 0 and < 16 or >= 0x80 and <= 0x94
        ? code : throw new UnsupportedInstruction("Invalid or reserved DVD register code.");

    private static DvdVmNavigation DecodeLink(Fields f)
    {
        var opcode = f.Byte(1, 15);
        if (opcode == 4) return Target(DvdVmNavigationKind.ProgramChain, f.Byte(6, 0x7f) << 8 | f.Byte(7));
        var button = f.Byte(6, 0xfc) >> 2;
        if (button > 36) throw new UnsupportedInstruction("Link button exceeds the supported DVD button count.");
        return opcode switch
        {
            1 => new(f.Byte(7, 31) switch
            {
                0 => DvdVmNavigationKind.SelectButton, 1 => DvdVmNavigationKind.TopCell, 2 => DvdVmNavigationKind.NextCell,
                3 => DvdVmNavigationKind.PreviousCell, 5 => DvdVmNavigationKind.TopProgram, 6 => DvdVmNavigationKind.NextProgram,
                7 => DvdVmNavigationKind.PreviousProgram, 9 => DvdVmNavigationKind.TopProgramChain, 10 => DvdVmNavigationKind.NextProgramChain,
                11 => DvdVmNavigationKind.PreviousProgramChain, 12 => DvdVmNavigationKind.GoUpProgramChain,
                13 => DvdVmNavigationKind.TailProgramChain, 16 => DvdVmNavigationKind.Resume,
                _ => throw new UnsupportedInstruction("Unknown DVD link subinstruction.")
            }, Button: button),
            5 => Target(DvdVmNavigationKind.Chapter, f.Byte(6, 3) << 8 | f.Byte(7), button),
            6 => Target(DvdVmNavigationKind.Program, f.Byte(7, 0x7f), button),
            7 => Target(DvdVmNavigationKind.Cell, f.Byte(7), button),
            _ => throw new UnsupportedInstruction("Unknown DVD link instruction.")
        };
    }

    private static DvdVmNavigation DecodeJump(Fields f)
    {
        var opcode = f.Byte(1, 15);
        if (opcode == 1) return new(DvdVmNavigationKind.Exit);
        if (opcode is 2 or 3) return Target(opcode == 2 ? DvdVmNavigationKind.GlobalTitle : DvdVmNavigationKind.TitleSetTitle, f.Byte(5, 0x7f));
        if (opcode == 5) return Target(DvdVmNavigationKind.TitleSetChapter, f.Byte(2, 3) << 8 | f.Byte(3)) with { Title = Positive(f.Byte(5, 0x7f)) };
        if (opcode is not (6 or 8)) throw new UnsupportedInstruction("Unknown DVD jump/call instruction.");
        var call = opcode == 8;
        var mode = f.Byte(5, 0xc0) >> 6;
        var resume = call ? f.Byte(4) : 0;
        return mode switch
        {
            0 => new(call ? DvdVmNavigationKind.CallFirstPlay : DvdVmNavigationKind.FirstPlay, ResumeCell: resume),
            1 => Target(call ? DvdVmNavigationKind.CallManagerMenu : DvdVmNavigationKind.ManagerMenu, Menu(f.Byte(5, 15))) with { ResumeCell = resume },
            2 when call => Target(DvdVmNavigationKind.CallTitleSetMenu, Menu(f.Byte(5, 15))) with { ResumeCell = resume },
            2 => Target(DvdVmNavigationKind.TitleSetMenu, Menu(f.Byte(5, 15))) with { TitleSet = f.Byte(4), Title = Positive(f.Byte(3)) },
            _ => Target(call ? DvdVmNavigationKind.CallManagerProgramChain : DvdVmNavigationKind.ManagerProgramChain,
                f.Byte(2, 0x7f) << 8 | f.Byte(3)) with { ResumeCell = resume }
        };
    }

    private static DvdVmNavigation Target(DvdVmNavigationKind kind, int value, int button = 0) => new(kind, Positive(value), button);
    private static int Positive(int value) => value > 0 ? value : throw new UnsupportedInstruction("A DVD navigation target cannot be zero.");
    private static int Menu(int value) => value is >= 2 and <= 7 ? value : throw new UnsupportedInstruction("Unknown DVD menu identifier.");
}
