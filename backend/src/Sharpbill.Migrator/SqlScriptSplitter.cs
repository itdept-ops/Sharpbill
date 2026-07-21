using System.Text;

namespace Sharpbill.Migrator;

internal static class SqlScriptSplitter
{
    public static IReadOnlyList<string> Split(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var statements = new List<string>();
        var current = new StringBuilder();
        ParserState state = ParserState.Normal;

        for (int index = 0; index < script.Length; index++)
        {
            char character = script[index];
            char next = index + 1 < script.Length ? script[index + 1] : '\0';

            switch (state)
            {
                case ParserState.Normal:
                    if (character == '\'' || character == '"' || character == '`')
                    {
                        state = character switch
                        {
                            '\'' => ParserState.SingleQuoted,
                            '"' => ParserState.DoubleQuoted,
                            _ => ParserState.BacktickQuoted,
                        };
                        current.Append(character);
                    }
                    else if (character == '#')
                    {
                        AppendCommentBoundary(current);
                        state = ParserState.LineComment;
                    }
                    else if (character == '-'
                             && next == '-'
                             && (index + 2 >= script.Length || char.IsWhiteSpace(script[index + 2])))
                    {
                        AppendCommentBoundary(current);
                        state = ParserState.LineComment;
                        index++;
                    }
                    else if (character == '/' && next == '*')
                    {
                        AppendCommentBoundary(current);
                        state = ParserState.BlockComment;
                        index++;
                    }
                    else if (character == ';')
                    {
                        AddStatement(statements, current);
                    }
                    else
                    {
                        current.Append(character);
                    }

                    break;

                case ParserState.LineComment:
                    if (character is '\r' or '\n')
                    {
                        current.Append(character);
                        state = ParserState.Normal;
                    }

                    break;

                case ParserState.BlockComment:
                    if (character == '*' && next == '/')
                    {
                        state = ParserState.Normal;
                        index++;
                    }

                    break;

                case ParserState.SingleQuoted:
                case ParserState.DoubleQuoted:
                case ParserState.BacktickQuoted:
                    current.Append(character);
                    char terminator = state switch
                    {
                        ParserState.SingleQuoted => '\'',
                        ParserState.DoubleQuoted => '"',
                        _ => '`',
                    };

                    if (character == '\\' && state != ParserState.BacktickQuoted && next != '\0')
                    {
                        current.Append(next);
                        index++;
                    }
                    else if (character == terminator && next == terminator)
                    {
                        current.Append(next);
                        index++;
                    }
                    else if (character == terminator)
                    {
                        state = ParserState.Normal;
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Unknown SQL parser state {state}.");
            }
        }

        if (state is ParserState.SingleQuoted
            or ParserState.DoubleQuoted
            or ParserState.BacktickQuoted
            or ParserState.BlockComment)
        {
            throw new FormatException("The SQL snapshot contains an unterminated quote or comment.");
        }

        AddStatement(statements, current);
        if (statements.Any(statement =>
                statement.StartsWith("DELIMITER", StringComparison.OrdinalIgnoreCase)))
        {
            throw new FormatException(
                "DELIMITER directives are not supported in the reviewed schema snapshot.");
        }

        return statements;
    }

    private static void AddStatement(List<string> statements, StringBuilder current)
    {
        string statement = current.ToString().Trim();
        current.Clear();
        if (statement.Length > 0)
        {
            statements.Add(statement);
        }
    }

    private static void AppendCommentBoundary(StringBuilder current)
    {
        if (current.Length > 0 && !char.IsWhiteSpace(current[^1]))
        {
            current.Append(' ');
        }
    }

    private enum ParserState
    {
        Normal,
        SingleQuoted,
        DoubleQuoted,
        BacktickQuoted,
        LineComment,
        BlockComment,
    }
}
