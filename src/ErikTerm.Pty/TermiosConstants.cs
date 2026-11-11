namespace ErikTerm.Pty;

/// <summary>
/// The termios layout and flag values raw mode needs, one table per OS (same idea as <see cref="PtyConstants"/>).
/// Linux: asm-generic/termbits.h (x86_64 and arm64 agree; tcflag_t is 32-bit, c_cc starts at byte 17 after c_line).
/// macOS: bsd/sys/termios.h (tcflag_t is 64-bit, so the flag fields are 8 bytes each and c_cc starts at byte 32).
/// </summary>
internal sealed record TermiosConstants(
    string Os,
    int Size,
    int FlagWidth,
    int CcOffset,
    int VMIN,
    int VTIME,
    ulong BRKINT,
    ulong ICRNL,
    ulong INPCK,
    ulong ISTRIP,
    ulong IXON,
    ulong OPOST,
    ulong CS8,
    ulong ECHO,
    ulong ICANON,
    ulong IEXTEN,
    ulong ISIG,
    ulong TIOCGWINSZ)
{
    public static readonly TermiosConstants Linux = new(
        Os: "linux",
        Size: 60,                 // glibc's struct termios: 4 flags, c_line, c_cc[32], 2 speeds
        FlagWidth: 4,
        CcOffset: 17,
        VMIN: 6,
        VTIME: 5,
        BRKINT: 0x2, ICRNL: 0x100, INPCK: 0x10, ISTRIP: 0x20, IXON: 0x400,
        OPOST: 0x1,
        CS8: 0x30,
        ECHO: 0x8, ICANON: 0x2, IEXTEN: 0x8000, ISIG: 0x1,
        TIOCGWINSZ: 0x5413);

    public static readonly TermiosConstants MacOS = new(
        Os: "macos",
        Size: 72,                 // 4 × 8-byte flags, c_cc[20], 2 × 8-byte speeds
        FlagWidth: 8,
        CcOffset: 32,
        VMIN: 16,
        VTIME: 17,
        BRKINT: 0x2, ICRNL: 0x100, INPCK: 0x10, ISTRIP: 0x20, IXON: 0x200,
        OPOST: 0x1,
        CS8: 0x300,
        ECHO: 0x8, ICANON: 0x100, IEXTEN: 0x400, ISIG: 0x80,
        TIOCGWINSZ: 0x40087468);   // _IOR('t', 104, struct winsize)

    public static TermiosConstants For(bool macOS) => macOS ? MacOS : Linux;

    public static readonly TermiosConstants Current = For(OperatingSystem.IsMacOS());

    /// <summary>Indexes of the four flag words (c_iflag, c_oflag, c_cflag, c_lflag).</summary>
    public const int IFlag = 0, OFlag = 1, CFlag = 2, LFlag = 3;

    /// <summary>
    /// cfmakeraw, applied to a copy of the saved termios bytes: no echo or canonical input, no signals from the
    /// keyboard (Ctrl+C arrives as a key, so an unsaved merge can ask first), no output post-processing.
    /// </summary>
    public void MakeRaw(Span<byte> termios)
    {
        ulong iflag = Flag(termios, IFlag) & ~(BRKINT | ICRNL | INPCK | ISTRIP | IXON);
        ulong oflag = Flag(termios, OFlag) & ~OPOST;
        ulong cflag = Flag(termios, CFlag) | CS8;
        ulong lflag = Flag(termios, LFlag) & ~(ECHO | ICANON | IEXTEN | ISIG);
        SetFlag(termios, IFlag, iflag);
        SetFlag(termios, OFlag, oflag);
        SetFlag(termios, CFlag, cflag);
        SetFlag(termios, LFlag, lflag);
        termios[CcOffset + VMIN] = 1;
        termios[CcOffset + VTIME] = 0;
    }

    public ulong Flag(ReadOnlySpan<byte> termios, int index) => FlagWidth == 4
        ? BitConverter.ToUInt32(termios[(index * 4)..])
        : BitConverter.ToUInt64(termios[(index * 8)..]);

    public void SetFlag(Span<byte> termios, int index, ulong value)
    {
        if (FlagWidth == 4) BitConverter.TryWriteBytes(termios[(index * 4)..], (uint)value);
        else BitConverter.TryWriteBytes(termios[(index * 8)..], value);
    }
}
