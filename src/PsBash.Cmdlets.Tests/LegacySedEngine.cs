using System.Text;
using System.Text.RegularExpressions;
using static PsBash.Cmdlets.InvokeBashSedCommand;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// VERBATIM copy of the whole-input sed cycle engine (<c>InvokeBashSedCommand.ProcessLines</c> + its address helpers)
/// as it stood before the streaming rewrite. It is the reference oracle for <see cref="SedEngineEquivalenceTests"/>:
/// the push state machine must produce byte-identical output records for every script and input.
/// </summary>
internal static class LegacySedEngine
{    /// <summary>
    /// Reproduces the psm1 <c>Test-SedAddress</c>.
    /// </summary>
    private static bool TestAddress(
        SedCommand cmd, string line, int lineNum, string[] allLines)
    {
        var addr = cmd.Address;
        if (addr == null)
        {
            return true;
        }

        switch (addr.Type)
        {
            case AddressType.Regex:
                return Regex.IsMatch(line, addr.Pattern!);
            case AddressType.Line:
                return lineNum == addr.Line;
            case AddressType.Last:
                return lineNum == allLines.Length;
            case AddressType.RangeNum:
                return lineNum >= addr.Start && lineNum <= addr.End;
            case AddressType.Step:
            {
                // first~step: match `first`, then every step-th line after it.
                // step <= 0 degenerates to just `first` (GNU). first may be 0,
                // in which case 0~step matches multiples of step.
                if (addr.Step <= 0) { return lineNum == addr.Start; }
                return lineNum >= Math.Max(addr.Start, 1)
                    && (lineNum - addr.Start) % addr.Step == 0;
            }
            case AddressType.RangeNumToRegex:
            {
                // N,/re/ — active from line max(Start,1); ends on the first line
                // whose text matches the end regex (inclusive). The 0,/re/ idiom
                // (Start == 0) lets the end regex match the very first line.
                int begin = Math.Max(addr.Start, 1);
                if (lineNum < begin) { return false; }
                bool canEndOnStart = addr.Start == 0;
                for (int ri = begin; ri <= lineNum && ri <= allLines.Length; ri++)
                {
                    if ((ri > begin || canEndOnStart)
                        && Regex.IsMatch(allLines[ri - 1], addr.EndPattern!))
                    {
                        // Range closed at line ri — only lines begin..ri are in range.
                        return lineNum <= ri;
                    }
                }
                return true; // end regex never matched up to here — still in range.
            }
            case AddressType.RangeRegex:
            {
                bool inRange = false;
                bool rangeActive = false;
                for (int ri = 0; ri < allLines.Length; ri++)
                {
                    if (!rangeActive
                        && Regex.IsMatch(allLines[ri], addr.StartPattern!))
                    {
                        rangeActive = true;
                    }
                    if (rangeActive && ri + 1 == lineNum)
                    {
                        inRange = true;
                    }
                    if (rangeActive && ri + 1 != lineNum
                        && Regex.IsMatch(allLines[ri], addr.EndPattern!))
                    {
                        rangeActive = false;
                    }
                    if (rangeActive && ri + 1 == lineNum
                        && Regex.IsMatch(allLines[ri], addr.EndPattern!))
                    {
                        rangeActive = false;
                    }
                    if (ri + 1 > lineNum)
                    {
                        break;
                    }
                }
                return inRange;
            }
        }
        return false;
    }

    /// <summary>
    /// True when a <c>c</c> command's address is a RANGE and the line after
    /// <paramref name="lineNum"/> is still inside that range. Used so the change
    /// text is emitted once per range rather than once per line. A non-range
    /// address always returns false (emit on every matching line).
    /// </summary>
    private static bool RangeContinuesPast(
        SedCommand cmd, string firstLine, int lineNum, string[] allLines)
    {
        var addr = cmd.Address;
        if (addr is null) { return false; }
        switch (addr.Type)
        {
            case AddressType.RangeNum:
            case AddressType.RangeRegex:
            case AddressType.RangeNumToRegex:
                break;
            default:
                return false;
        }

        if (lineNum >= allLines.Length) { return false; }
        string nextFirst = allLines[lineNum];
        bool nextMatched = TestAddress(cmd, nextFirst, lineNum + 1, allLines);
        return cmd.Negate ? !nextMatched : nextMatched;
    }

    /// <summary>
    /// Reproduces the psm1 <c>$processLines</c> closure: the sed cycle engine.
    /// </summary>
    public static List<string> ProcessLines(
        string[] inputLines, List<SedCommand> commands)
    {
        var outputLines = new List<string>();
        int totalLines = inputLines.Length;
        int li = 0;

        while (li < totalLines)
        {
            string patternSpace = inputLines[li];
            int lineNum = li + 1;
            bool quit = false;

            bool restartCycle = true;
            while (restartCycle)
            {
                restartCycle = false;
                var printedLines = new List<string>();
                var appendTexts = new List<string>();
                var insertTexts = new List<string>();
                bool deleted = false;

                foreach (var cmd in commands)
                {
                    if (deleted)
                    {
                        break;
                    }
                    if (quit && cmd.Type != 'q' && cmd.Type != 'Q')
                    {
                        continue;
                    }

                    string firstLine = patternSpace.Contains('\n')
                        ? patternSpace.Substring(0, patternSpace.IndexOf('\n'))
                        : patternSpace;

                    bool matched = TestAddress(cmd, firstLine, lineNum, inputLines);
                    if (cmd.Negate) { matched = !matched; }
                    if (!matched)
                    {
                        continue;
                    }

                    switch (cmd.Type)
                    {
                        case 's':
                        {
                            // Walk every match and decide per-occurrence whether to
                            // substitute, so the four GNU forms all fall out of one
                            // path: first-only (count == 1), g (count >= 1), Nth
                            // (count == N), and Ng (count >= N).
                            int target = cmd.Nth > 0 ? cmd.Nth : 1;
                            int count = 0;
                            bool subbed = false;
                            patternSpace = cmd.Regex!.Replace(patternSpace, m =>
                            {
                                count++;
                                bool hit = cmd.Global ? count >= target : count == target;
                                if (hit) { subbed = true; }
                                return hit ? m.Result(cmd.Replacement!) : m.Value;
                            });
                            // s///p: print the pattern space only if a sub happened.
                            if (cmd.PrintOnSub && subbed) { printedLines.Add(patternSpace); }
                            break;
                        }
                        case 'd':
                            deleted = true;
                            break;
                        case 'D':
                        {
                            int nlIdx = patternSpace.IndexOf('\n');
                            if (nlIdx >= 0)
                            {
                                patternSpace = patternSpace.Substring(nlIdx + 1);
                            }
                            else
                            {
                                deleted = true;
                                patternSpace = string.Empty;
                            }
                            if (!deleted && patternSpace.Length > 0)
                            {
                                restartCycle = true;
                            }
                            break;
                        }
                        case 'p':
                            printedLines.Add(patternSpace);
                            break;
                        case 'P':
                        {
                            int nlIdx = patternSpace.IndexOf('\n');
                            printedLines.Add(nlIdx >= 0
                                ? patternSpace.Substring(0, nlIdx)
                                : patternSpace);
                            break;
                        }
                        case 'N':
                            li++;
                            if (li < totalLines)
                            {
                                patternSpace += "\n" + inputLines[li];
                            }
                            else
                            {
                                // No next line: GNU sed ends the run here without
                                // executing later commands. Auto-print still
                                // applies (so `N;s…` emits the final line), but
                                // -n suppresses it (so `-n 'N;p'` emits nothing
                                // more). Later commands are skipped via `quit`.
                                quit = true;
                            }
                            break;
                        case 'q':
                            quit = true;
                            break;
                        case 'Q':
                            // Quit immediately WITHOUT auto-printing this line.
                            quit = true;
                            deleted = true;
                            break;
                        case '=':
                            // Print the current line number (before the line text).
                            printedLines.Add(lineNum.ToString());
                            break;
                        case 'a':
                            appendTexts.Add(cmd.Text!);
                            break;
                        case 'i':
                            insertTexts.Add(cmd.Text!);
                            break;
                        case 'c':
                            deleted = true;
                            // A range `c` prints its text ONCE for the whole range,
                            // at the range's final line (GNU); a per-line `c` prints
                            // it on every matching line. TestAddress matched this
                            // line, so emit unless a LATER line is still in range.
                            if (!RangeContinuesPast(cmd, firstLine, lineNum, inputLines))
                            {
                                appendTexts.Add(cmd.Text!);
                            }
                            break;
                        case 'y':
                        {
                            var sb = new StringBuilder(patternSpace.Length);
                            foreach (char ch in patternSpace)
                            {
                                int idx = cmd.Source!.IndexOf(ch);
                                sb.Append(idx >= 0 ? cmd.Dest![idx] : ch);
                            }
                            patternSpace = sb.ToString();
                            break;
                        }
                    }

                    if (restartCycle)
                    {
                        break;
                    }
                }

                if (restartCycle)
                {
                    continue;
                }

                foreach (var insText in insertTexts)
                {
                    outputLines.Add(insText);
                }
                foreach (var pLine in printedLines)
                {
                    outputLines.Add(pLine);
                }
                if (!deleted)
                {
                    // Suppression: -n was modeled by the psm1 oracle's
                    // $suppressDefault. ProcessLines receives that decision
                    // through the caller; emulate by checking the sentinel
                    // below. (See EndProcessing — suppressDefault is folded in
                    // via the SuppressDefault field on the first call.)
                    if (!SuppressDefault)
                    {
                        if (patternSpace.Contains('\n'))
                        {
                            foreach (var psLine in patternSpace.Split('\n'))
                            {
                                outputLines.Add(psLine);
                            }
                        }
                        else
                        {
                            outputLines.Add(patternSpace);
                        }
                    }
                }
                foreach (var appText in appendTexts)
                {
                    outputLines.Add(appText);
                }
            }

            li++;

            if (quit)
            {
                break;
            }
        }

        return outputLines;
    }

    /// <summary>
    /// The <c>-n</c> suppress-default-output flag. <see cref="ProcessLines"/> is
    /// static (it is a pure transform); this instance-set, statically-read field
    /// threads the flag in without changing the method signature. It is set once
    /// per cmdlet invocation in <see cref="EndProcessing"/> before any
    /// <see cref="ProcessLines"/> call. Cmdlet instances do not run concurrently
    /// in a single runspace, so a static carrier is safe here.
    /// </summary>
    [ThreadStatic]
    internal static bool SuppressDefault;


}

