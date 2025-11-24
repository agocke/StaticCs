using System;
using System.Runtime.CompilerServices;
using System.Text;

#if !NET6_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    internal sealed class InterpolatedStringHandlerAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class InterpolatedStringHandlerArgumentAttribute : Attribute
    {
        public InterpolatedStringHandlerArgumentAttribute(string argument)
        {
            Arguments = new string[] { argument };
        }

        public InterpolatedStringHandlerArgumentAttribute(params string[] arguments)
        {
            Arguments = arguments;
        }

        public string[] Arguments { get; }
    }
}
#endif

namespace StaticCs
{
/// <summary>
/// A string builder with automatic indentation support for multi-line text.
/// Manages indentation levels with <see cref="Indent"/> and <see cref="Dedent"/> methods,
/// and automatically applies the current indentation to appended content and interpolated strings.
/// </summary>
public sealed class IndentingBuilder : IComparable<IndentingBuilder>, IEquatable<IndentingBuilder>
{
    public static readonly Encoding UTF8Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private string _currentIndentWhitespace = "";
    private StringBuilder _stringBuilder;

    public IndentingBuilder(string s)
    {
        _stringBuilder = new StringBuilder(s);
    }

    public IndentingBuilder(SourceBuilderStringHandler s)
    {
        _currentIndentWhitespace = "";
        _stringBuilder = s._stringBuilder;
    }

    public IndentingBuilder()
    {
        _stringBuilder = new StringBuilder();
    }

    /// <summary>
    /// Removes trailing whitespace from every line and replace all newlines with
    /// Environment.NewLine.
    /// </summary>
    private void Normalize()
    {
        _stringBuilder.Replace("\r\n", "\n");

        // Remove trailing whitespace from every line
        int wsStart;
        for (int i = 0; i < _stringBuilder.Length; i++)
        {
            if (_stringBuilder[i] is '\n')
            {
                wsStart = i - 1;
                while (wsStart >= 0 && (_stringBuilder[wsStart] is ' ' or '\t'))
                {
                    wsStart--;
                }
                wsStart++; // Move back to first whitespace
                if (wsStart < i)
                {
                    int len = i - wsStart;
                    _stringBuilder.Remove(wsStart, len);
                    i -= len;
                }
            }
        }

        _stringBuilder.Replace("\n", Environment.NewLine);
    }

    public override string ToString()
    {
        Normalize();
        return _stringBuilder.ToString();
    }

    public void Append(
        [InterpolatedStringHandlerArgument("")]
        SourceBuilderStringHandler s)
    {
        // No work needed, the handler has already added the text to the string builder
    }

    public void Append(string s)
    {
        _stringBuilder.Append(_currentIndentWhitespace);
        Append(_stringBuilder, _currentIndentWhitespace, s);
    }

    public void Append(IndentingBuilder srcBuilder)
    {
        Append(srcBuilder.ToString());
    }

    private static void Append(
        StringBuilder builder,
        string currentIndentWhitespace,
        string str)
    {
        int start = 0;
        int nl;
        while (start < str.Length)
        {
            nl = str.IndexOf('\n', start);
            if (nl == -1)
            {
                nl = str.Length;
            }
            // Skip blank lines
            while (nl < str.Length && (str[nl] == '\n' || str[nl] == '\r'))
            {
                nl++;
            }
            if (start > 0)
            {
                builder.Append(currentIndentWhitespace);
            }
            builder.Append(str, start, nl - start);
            start = nl;
        }
    }

    public void AppendLine(
        [InterpolatedStringHandlerArgument("")]
        SourceBuilderStringHandler s)
    {
        Append(s);
        _stringBuilder.AppendLine();
    }

    public void AppendLine(string s)
    {
        Append(s);
        _stringBuilder.AppendLine();
    }

    public int CompareTo(IndentingBuilder? other)
    {
        if (other is null) return 1;

        var lenCmp = _stringBuilder.Length.CompareTo(other._stringBuilder.Length);
        if (lenCmp != 0)
        {
            return lenCmp;
        }
        for (int i = 0; i < _stringBuilder.Length; i++)
        {
            var cCmp = _stringBuilder[i].CompareTo(other._stringBuilder[i]);
            if (cCmp != 0)
            {
                return cCmp;
            }
        }
        return 0;
    }

    public void Indent()
    {
        _currentIndentWhitespace += "    ";
    }

    public void Dedent()
    {
        _currentIndentWhitespace = _currentIndentWhitespace.Substring(0, _currentIndentWhitespace.Length - 4);
    }

    public bool Equals(IndentingBuilder? other)
    {
        return _stringBuilder.Equals(other?._stringBuilder);
    }

    public void AppendLine(IndentingBuilder deserialize)
    {
        Append(deserialize);
        _stringBuilder.AppendLine();
    }

    [InterpolatedStringHandler]
    public ref struct SourceBuilderStringHandler
    {
        internal readonly StringBuilder _stringBuilder;
        private readonly string _originalIndentWhitespace;
        private string _currentIndentWhitespace;
        private bool _isFirst = true;

        public SourceBuilderStringHandler(int literalLength, int formattedCount)
        {
            _stringBuilder = new StringBuilder(literalLength);
            _originalIndentWhitespace = "";
            _currentIndentWhitespace = "";
        }

        public SourceBuilderStringHandler(
            int literalLength,
            int formattedCount,
            IndentingBuilder sourceBuilder)
        {
            _stringBuilder = sourceBuilder._stringBuilder;
            _originalIndentWhitespace = sourceBuilder._currentIndentWhitespace;
            _currentIndentWhitespace = sourceBuilder._currentIndentWhitespace;
        }

        public void AppendLiteral(string s)
        {
            if (_isFirst)
            {
                _stringBuilder.Append(_currentIndentWhitespace);
                _isFirst = false;
            }
            Append(_stringBuilder, _currentIndentWhitespace, s);

            int last = s.LastIndexOf('\n');
            if (last == -1)
            {
                return;
            }

            var remaining = s.Substring(last + 1);
            foreach (var c in remaining)
            {
                if (c != ' ' && c != '\t')
                {
                    return;
                }
            }

            _currentIndentWhitespace += remaining;
        }

        public void AppendFormatted<T>(T value)
        {
            if (_isFirst)
            {
                _stringBuilder.Append(_currentIndentWhitespace);
                _isFirst = false;
            }
            var str = value?.ToString();
            if (str is null)
            {
                _stringBuilder.Append(str);
                return;
            }

            Append(_stringBuilder, _currentIndentWhitespace, str);
            _currentIndentWhitespace = _originalIndentWhitespace;
        }
    }
}
}