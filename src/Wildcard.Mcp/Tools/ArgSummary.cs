using System.Text;

namespace Wildcard.Mcp.Tools;

internal sealed class ArgSummary
{
    private readonly StringBuilder _sb = new();
    private bool _first = true;

    private ArgSummary() => _sb.Append('[');

    public static ArgSummary Create() => new();

    public ArgSummary Arg(string name, string? value)
    {
        if (value is null) return this;
        Append(name);
        _sb.Append('"');
        _sb.Append(value.Length > 80 ? value[..77] + "..." : value);
        _sb.Append('"');
        return this;
    }

    public ArgSummary Arg(string name, string[]? value)
    {
        if (value is null or { Length: 0 }) return this;
        Append(name);
        _sb.Append('[');
        for (int i = 0; i < value.Length; i++)
        {
            if (i > 0) _sb.Append(", ");
            _sb.Append('"');
            _sb.Append(value[i]);
            _sb.Append('"');
        }
        _sb.Append(']');
        return this;
    }

    public ArgSummary Arg(string name, bool value, bool defaultValue)
    {
        if (value == defaultValue) return this;
        Append(name);
        _sb.Append(value ? "true" : "false");
        return this;
    }

    public ArgSummary Arg(string name, int value, int defaultValue)
    {
        if (value == defaultValue) return this;
        Append(name);
        _sb.Append(value);
        return this;
    }

    private void Append(string name)
    {
        _sb.Append(_first ? "" : ", ");
        _first = false;
        _sb.Append(name);
        _sb.Append('=');
    }

    public override string ToString()
    {
        return _sb.ToString() + "]\n";
    }
}
