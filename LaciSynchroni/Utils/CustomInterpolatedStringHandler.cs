using System.Runtime.CompilerServices;
using System.Text;

namespace LaciSynchroni.Utils;

[InterpolatedStringHandler]
#pragma warning disable CS9113 // Parameter 'formattedCount' is required by InterpolatedStringHandler contract
public readonly ref struct CustomInterpolatedStringHandler(int literalLength, int formattedCount)
#pragma warning restore CS9113
{
    readonly StringBuilder _logMessageStringbuilder = new(literalLength);

    public void AppendLiteral(string s)
    {
        _logMessageStringbuilder.Append(s);
    }

    public void AppendFormatted<T>(T t)
    {
        _logMessageStringbuilder.Append(t);
    }

    public string BuildMessage() => _logMessageStringbuilder.ToString();
}
