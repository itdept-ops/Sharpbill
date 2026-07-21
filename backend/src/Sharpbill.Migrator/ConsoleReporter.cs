namespace Sharpbill.Migrator;

internal sealed class ConsoleReporter
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public ConsoleReporter()
        : this(Console.Out, Console.Error)
    {
    }

    internal ConsoleReporter(TextWriter output, TextWriter error)
    {
        _output = output;
        _error = error;
    }

    public void Info(string code, string message)
    {
        _output.WriteLine($"[{code}] {message}");
    }

    public void Error(string code, string message)
    {
        _error.WriteLine($"[{code}] {message}");
    }
}
