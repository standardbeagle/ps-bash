using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// 	est / [ expression evaluator vs bash 5.2 (oracle: wsl bash, one row per argv, words split on '|').
/// Row = words TAB exit-status TAB diagnostic. 276 rows: every argument-count shape (0..5+), the
/// dash-leading operands that used to be eaten as options, -a/-o/! chains, parentheses, and the
/// usage errors. File predicates are stubbed false (they are covered by the filesystem tests).
/// </summary>
public class BashTestExprOracleTests
{
    private const string Table = """
	1	
x	0	
-n	0	
-e	0	
-z	0	
-x	0	
!	0	
(	0	
)	0	
-a	0	
-o	0	
a|b	2	a: unary operator expected
!|x	1	
!|	0	
!|!	1	
-n|x	0	
-z|x	1	
-z|	0	
-n|	1	
-e|/nonexistent-zz	1	
-f|/nonexistent-zz	1	
-x|/nonexistent-zz	1	
-q|x	2	-q: unary operator expected
-|x	2	-: unary operator expected
--|x	2	--: unary operator expected
a|=	2	a: unary operator expected
=|=	2	=: unary operator expected
-n|-n	0	
-z|-z	1	
-e|-e	1	
-eq|-eq	2	-eq: unary operator expected
!|-n	1	
!|-z	1	
a|=|a	0	
a|=|b	1	
a|==|a	0	
a|!=|a	1	
a|!=|b	0	
-f|=|-f	0	
=|=|=	0	
!|=|!	0	
-n|=|-n	0	
-a|=|-a	0	
-o|=|-o	0	
(|=|(	0	
!|!|!	0	
1|-eq|1	0	
1|-eq|2	1	
1|-ne|2	0	
1|-lt|2	0	
2|-le|2	0	
3|-gt|2	0	
2|-ge|3	1	
01|-eq|1	0	
+1|-eq|1	0	
-1|-lt|0	0	
 1 |-eq|1	0	
1|-eq|x	2	x: integer expression expected
x|-eq|1	2	x: integer expression expected
x|-eq|y	2	x: integer expression expected
|-eq|1	2	: integer expression expected
1.5|-eq|1	2	1.5: integer expression expected
0x10|-eq|16	2	0x10: integer expression expected
9223372036854775807|-gt|1	0	
9223372036854775808|-gt|1	2	9223372036854775808: integer expression expected
a|<|b	0	
b|<|a	1	
a|>|b	1	
a|<|a	1	
a|-a|b	0	
a||-a|b	2	too many arguments
|-a|b	1	
|-a|	1	
a|-o|b	0	
|-o|b	0	
|-o|	1	
x|-a|	1	
-a|-a|-a	0	
-o|-o|-o	0	
-a|-o|-a	0	
!|-a|!	0	
-n|-e|-a|-n|x	0	
-n|x|-a|-n|y	0	
-n|x|-a|-z|y	1	
-z|x|-o|-n|y	0	
-z|x|-o|-z|y	1	
!|-z|x	0	
!|-n|x	1	
!|-z	1	
!|x|=|y	0	
!|x|=|x	1	
!|!|x	0	
!|!|!|x	1	
!|!|!|!|x	0	
!|-z|x|-a|-n|y	0	
!|-z|x|-o|-z|y	0	
a|=|a|-a|b|=|b	0	
a|=|b|-o|c|=|c	0	
a|=|b|-a|c|=|c	1	
a|=|a|-a|b|=|c	1	
a|=|a|-o|x	0	
1|-lt|2|-a|3|-gt|2	0	
1|-lt|2|-o|x|-eq|y	2	x: integer expression expected
(|-n|x|)	0	
(|)	2	(: unary operator expected
(|x|)	0	
(|x|y|)	2	x: unary operator expected
(|-n|x|)|-a|-n|y	0	
(|-n|x|)|-o|-z|y	0	
!|(|-n|x|)	1	
!|(|-z|x|)	0	
(|-n|x	2	-n: binary operator expected
(|x	2	(: unary operator expected
(|-n|x|-a|y	2	`)' expected
(|!|-n|x|)	1	
(|(|x|)|)	0	
(|(|-n|x|)|)	0	
(|(|-n|x|)|)|-a|y	0	
-n|(|)	2	(: binary operator expected
-n|x|y	2	x: binary operator expected
-n|x|y|z	2	too many arguments
x|y|z	2	y: binary operator expected
x|y|z|w	2	too many arguments
a|b	2	a: unary operator expected
a|b|c	2	b: binary operator expected
a|=|b|c	2	too many arguments
a|=|b|c|d	2	too many arguments
1|-eq|1|2	2	too many arguments
-n|-n|-n	2	-n: binary operator expected
-n|-n|-n|-n	2	syntax error: `-n' unexpected
-z|-z|-z	2	-z: binary operator expected
-z|-z|-z|-z	2	syntax error: `-z' unexpected
-e|-e|-e|-e	2	syntax error: `-e' unexpected
-a|-a	1	
-o|-o	1	
-a|-a|-a|-a	1	
-o|-o|-o|-o	0	
-a|-a|-a|-a|-a	1	
-n|-a|-n	0	
-n|-o|-n	0	
-z|-a|-z	0	
-z|-o|-z	0	
-e|-a|-e	0	
-f|-o|-f	0	
-o|-a|-o	0	
-a|-o|-a	0	
-a|-a|x	0	
-a|-n|-a	2	-n: binary operator expected
-n|-a|-a	0	
-n|-o|-a	0	
-n|-n|-a|-n	0	
-n|-n|-o|-z	0	
!|-a	1	
!|-o	1	
!|-a|-a	0	
-a|!	1	
-o|!	1	
x|-a|!	0	
x|-o|!	0	
!|x|-a|y	1	
!|x|-o|y	1	
!|-n	1	
!|-n|-n	1	
!|-e|-e	0	
!|-e|-f	0	
!|-n|-e|-a|-f	1	
-t	0	
-t|x	1	
-t|99	1	
-v|HOME	0	
-v|NOPE_NOT_SET_ZZ	1	
-v	0	
-R|x	1	
-o|noclobber	1	
-o|nosuchopt	1	
-nt|-nt|-nt	1	
a|-nt|b	1	
a|-ot|b	1	
a|-ef|b	1	
a|-ef|a	1	
-eq|-eq|-eq	2	-eq: integer expression expected
-z|-eq|-z	2	-z: integer expression expected
-n|-ne|-n	2	-n: integer expression expected
-n|-gt|-n	2	-n: integer expression expected
-n|-gt|1	2	-n: integer expression expected
1|-gt|-n	2	-n: integer expression expected
-a|-eq|-a	2	-a: integer expression expected
-e|-eq|1	2	-e: integer expression expected
1|-eq|-e	2	-e: integer expression expected
!|1|-eq|1	1	
!|1|-eq|2	0	
!|x|-eq|1	2	x: integer expression expected
1|-eq|1|-a|2|-eq|2	0	
1|-eq|1|-o|x|-eq|2	2	x: integer expression expected
x|-eq|1|-o|2|-eq|2	2	x: integer expression expected
1|=|1|-a|-n	0	
-n|-a|1|=|1	2	too many arguments
-n|x|-a|1|=|1	0	
-n|-a|-n|-a|-n	2	syntax error: `-n' unexpected
-n|-a|-n|-a|-n|-a|-n	2	syntax error: `-n' unexpected
-n|-a|-n|-o|-n|-a|-n	2	syntax error: `-n' unexpected
-z|-o|-z|-o|-z|-o|-z	2	syntax error: `-z' unexpected
a|-o|b|-a|c	0	
a|-a|b|-o|c	0	
|-a|b|-o|c	0	
|-a|b|-o|	1	
|-a|b|-o|-n	0	
!|!|-n	0	
!|!|-n|x	0	
-n|!|x	2	!: binary operator expected
x|=|!	1	
!|=|x	1	
!|!=|x	0	
!|!=|!	1	
!|-eq|!	2	!: integer expression expected
(|=|)	1	
)|=|)	0	
(|!=|)	0	
x|=|y|=|z	2	too many arguments
x|=|x|=|x	2	too many arguments
1|-eq|1|-eq|1	2	syntax error: `-eq' unexpected
-e|f|-a	2	f: binary operator expected
-n||	2	: binary operator expected
-n||x	2	: binary operator expected
-z||	2	: binary operator expected
|=|	0	
|!=|	1	
|=|x	1	
|-eq|	2	: integer expression expected
|-lt|	2	: integer expression expected
!||	2	: unary operator expected
!|-n|	0	
!|-z|	1	
-n|-z|	2	-z: binary operator expected
a|=|b|)|c	2	too many arguments
a|=|b|(|c	2	too many arguments
a|=|b|!|c	2	too many arguments
a|=|b|-foo|c	2	syntax error: `-foo' unexpected
a|=|b|-nt|c	2	syntax error: `-nt' unexpected
a|=|b|-x|c	2	syntax error: `-x' unexpected
a|=|b|-ab|c	2	syntax error: `-ab' unexpected
a|=|b|-a|c	1	
a|=|b|-o|c	0	
-n|x|-foo|y|z	2	syntax error: `-foo' unexpected
-n|x|--|y|z	2	syntax error: `--' unexpected
-n|x|-a|-o|z	1	
x|=|y|-x|z	2	syntax error: `-x' unexpected
x|=|y|-ab|z	2	syntax error: `-ab' unexpected
x|=|y|-eq|z	2	syntax error: `-eq' unexpected
-n|-t|x	2	-t: binary operator expected
-t|0|-a|x	1	
x|-a|-t	0	
-n|x|-a|-n	0	
-n|x|-a|-z	0	
-z|x|-a	2	x: binary operator expected
x|-o|-z	0	
-a|x	1	
-o|x	1	
!|-a|x	0	
!|-o|x	0	
(|-a|)	0	
(|-o|)	0	
(|!|)	0	
!|(|)	2	(: unary operator expected
!|(|x|)	1	
(|x|)|-a|(|y|)	0	
(|(|x|)|-a|(|y|)|)	0	
!|!|!|!	1	
!|!|!|!|!	2	argument expected
-n|x|-a|!	2	argument expected
x|-a|-n	0	
x|-a|-n|y|-o	2	argument expected
-eq	0	
-eq|x	2	-eq: unary operator expected
x|-eq	2	x: unary operator expected
""";

    public static IEnumerable<object[]> Rows()
    {
        foreach (var raw in Table.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var f = line.Split('\t');
            yield return new object[] { f[0], int.Parse(f[1]), f.Length > 2 ? f[2] : "" };
        }
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Eval_MatchesBash(string words, int exit, string message)
    {
        var args = words.Length == 0 ? Array.Empty<string>() : words.Split('|');
        int got;
        string msg = "";
        try
        {
            got = BashTestExpr.Eval(args,
                (op, operand) => op == "-v" && operand == "HOME",
                (op, l, r) => false) ? 0 : 1;
        }
        catch (TestSyntaxException ex) { got = 2; msg = ex.Message; }
        Assert.Equal((exit, message), (got, msg));
    }
}
