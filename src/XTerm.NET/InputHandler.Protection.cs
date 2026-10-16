using XTerm.Buffer;
using XTerm.Parser;

namespace XTerm;

/// <summary>
/// Character protection, both flavours. DECSCA (CSI Ps " q) marks characters against the
/// SELECTIVE erases, DECSED and DECSEL; ISO 6429's SPA/EPA (ESC V / ESC W) guards them against
/// the ordinary ED, EL and ECH. The two are independent -- xterm keeps them apart and esctest
/// tests them apart -- so a cell carries two bits, not one.
/// </summary>
public partial class InputHandler
{
    /// <summary>No protection in force -- erases honour nothing.</summary>
    private const int ProtectionOff = 0;

    /// <summary>ISO protection (SPA was used last): the PLAIN erases honour guard bits.</summary>
    private const int ProtectionIso = 1;

    /// <summary>DEC protection (DECSCA was used last): only the SELECTIVE erases honour bits.</summary>
    private const int ProtectionDec = 2;

    /// <summary>
    /// Which protection discipline is currently in force. xterm keeps this as a single global
    /// gate next to the per-cell bits: SPA raises ISO, DECSCA raises DEC (whatever its parameter),
    /// and DECSTR or RIS drops it to off -- at which point every erase ignores the bits still
    /// sitting in cells. esctest leans on that: its per-test reset is DECSTR then ED 2, and the
    /// ED must sweep away the guarded characters earlier tests left behind.
    /// </summary>
    private int _protectionMode;

    internal void ResetProtectionMode() => _protectionMode = ProtectionOff;

    /// <summary>
    /// Whether any guard or protection has ever been set this session. The erase paths are hot
    /// -- a full-screen clear fills every line -- and this flag is what lets them keep the plain
    /// block fill until the first program actually uses protection.
    /// </summary>
    private bool _protectionUsed;

    /// <summary>Blanks the whole visible screen with the erase attributes -- DECCOLM's clear.</summary>
    internal void EraseWholeScreen()
    {
        for (var row = 0; row < _terminal.Rows; row++)
            EraseLineCells(_buffer.Lines[_buffer.YBase + row], 0, _terminal.Cols, selective: false,
                           resetLineAttribute: true);
    }

    /// <summary>DECSCA. 1 protects what is written next; 0 and 2 stop protecting.</summary>
    private void SelectCharacterProtection(Params parameters)
    {
        var on = parameters.GetParam(0, 0) == 1;
        _curAttr.SetProtected(on);
        // Any DECSCA -- protecting or not -- selects the DEC discipline, exactly as xterm's
        // CASE_DECSCA sets protected_mode unconditionally.
        _protectionMode = ProtectionDec;
        if (on)
            _protectionUsed = true;
    }

    /// <summary>SPA (ESC V) -- what is written next is guarded against ED, EL and ECH.</summary>
    internal void StartProtectedArea()
    {
        _curAttr.SetGuarded(true);
        _protectionMode = ProtectionIso;
        _protectionUsed = true;
    }

    /// <summary>EPA (ESC W) -- ends the guarded run.</summary>
    internal void EndProtectedArea() => _curAttr.SetGuarded(false);

    /// <summary>
    /// Erases <paramref name="line"/> from <paramref name="start"/> up to (exclusive)
    /// <paramref name="end"/>, honouring whichever protection applies: guarded cells always
    /// survive, and DECSCA-protected cells survive the SELECTIVE erases.
    /// </summary>
    /// <param name="resetLineAttribute">
    /// Whether an erase that takes the whole line may also take its DECDWL/DECDHL attribute.
    /// Only ED asks for this; see the note on <c>wholeLine</c> below.
    /// </param>
    private void EraseLineCells(BufferLine? line, int start, int end, bool selective,
                                bool resetLineAttribute = false)
    {
        if (line is null)
            return;

        var blank = BufferCell.Space;
        blank.Attributes = GetEraseAttributes();

        // A line erased in full by ED goes back to single width. The attribute describes how
        // the line is DRAWN, and a line ED has cleared has nothing left to draw at double size
        // -- so carrying it forward only doubles whatever is written next. vttest's double-size
        // test made that visible: it erases the display between screens, so every screen after
        // it stayed doubled.
        //
        // ED ONLY, which is what resetLineAttribute carries in. EL and ECH erase a line without
        // ending it, and the attribute is a property of the line rather than of the text on it:
        // DEC keeps it across both, and vttest's double-size test is built to catch a terminal
        // that does not. It sets DECDHL on row 14, sends EL 2, and only then writes the text --
        // so a terminal that dropped the attribute here drew that row at normal size, leaving
        // the sheared bottom half on row 15 with no top half above it.
        //
        // Full and non-selective only, and only when nothing SURVIVED the erase. A partial
        // erase leaves text that is still meant to be double; a selective erase exists to
        // leave protected text standing; and under ISO protection a guarded cell survives
        // even a plain erase, which is why this is decided after the walk below rather than
        // before it -- resizing the line under surviving text is the same mistake in reverse.
        var wholeLine = resetLineAttribute && !selective && start == 0 && end >= _terminal.Cols;

        if (!_protectionUsed || _protectionMode == ProtectionOff)
        {
            line.Fill(blank, start, end);

            if (wholeLine)
                ClearedInFull(line);

            return;
        }

        var survived = false;

        for (var col = start; col < end && col < line.Length; col++)
        {
            var cell = line[col];
            // Under ISO, guard bits stop every erase, the selective ones included -- xterm's
            // documented deviation, which esctest's DECSED knownBug encodes. Under DEC, only
            // the selective erases honour DECSCA bits; a plain ED ploughs straight through.
            if ((_protectionMode == ProtectionIso && cell.Attributes.IsGuarded())
                || (selective && cell.Attributes.IsProtected()))
            {
                survived = true;
                continue;
            }

            line.SetCell(col, ref blank);
        }

        if (wholeLine && !survived)
            ClearedInFull(line);
    }

    /// <summary>
    /// What a line loses when a display erase has cleared it whole: its double-size attribute,
    /// and its shell integration marks. Reached from ED, and from <see cref="EraseWholeScreen"/>
    /// -- DECCOLM's clear, and the alternate screen blanked on the way out -- which are the
    /// other two acts that empty every row.
    /// </summary>
    /// <remarks>
    /// The marks are the deliberate exception to "a mark survives the erasing of the cells it sits
    /// among". That rule exists for EL, which shells use to redraw a prompt they have just marked.
    /// A display erase is a different act: <c>clear</c> is CSI 3 J, CUP, CSI 2 J -- the scrollback
    /// discarded and the screen blanked in place -- and the rows that come back blank still carried
    /// the marks of the commands that were on them. A host drawing a gutter from those marks kept
    /// painting prompt and exit bars beside empty rows. Kitty and Ghostty both drop a row's prompt
    /// flag when ED clears it; a shell that clears the screen from inside its own prompt loses that
    /// one mark under all three, and prints the prompt it marked onto a screen it just emptied
    /// either way. The cursor row of ED 0 and ED 1 goes through EL and keeps its marks, so a shell
    /// that homes the cursor and erases below -- zsh, after a redraw -- keeps the mark of the
    /// prompt it is about to print there.
    /// </remarks>
    private static void ClearedInFull(BufferLine line)
    {
        line.LineAttribute = Buffer.LineAttribute.Normal;
        line.ClearMarks();
    }
}
