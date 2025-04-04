namespace Shouldly.Internals;

class ActualCodeTextGetter : ICodeTextGetter
{
    private bool _determinedOriginatingFrame;
    private string? _shouldMethod;

    public int ShouldlyFrameOffset { get; private set; }
    public string? FileName { get; private set; }
    public int LineNumber { get; private set; }

    public string? GetCodeText(object? actual, StackTrace? stackTrace)
    {
        if (!ShouldlyConfiguration.IsSourceDisabledInErrors())
        {
            try
            {
                ParseStackTrace(stackTrace);
            }
            catch
            {
                // ignored
                // If we fail to parse the stack trace to determine the expression of the actual argument, we'll format the value instead
            }
        }

        return GetCodePart() ?? actual.ToStringAwesomely();
    }

    private void ParseStackTrace(StackTrace? stackTrace)
    {
        stackTrace ??= new(fNeedFileInfo: true);

        var frames =
            from index in Enumerable.Range(0, stackTrace.FrameCount)
            let frame = stackTrace.GetFrame(index)!
            let method = frame.GetMethod()
            where method is object && !method.IsSystemDynamicMachinery()
            select new { index, frame, method };

        var shouldlyFrame = frames
                                .SkipWhile(f => !f.method.IsShouldlyMethod())
                                .TakeWhile(f => f.method.IsShouldlyMethod())
                                .LastOrDefault()
                            ?? throw new InvalidOperationException("The stack trace did not contain a Shouldly method.");

        var originatingFrame = frames
                                   .FirstOrDefault(f => f.index > shouldlyFrame.index)
                               ?? throw new InvalidOperationException("The stack trace did not contain the caller of the Shouldly method.");

        ShouldlyFrameOffset = originatingFrame.index;

        var fileName = originatingFrame.frame.GetFileName();
        fileName = DeterministicBuildHelpers.ResolveDeterministicPaths(fileName);
        _shouldMethod = shouldlyFrame.method.Name;
        FileName = fileName;
        LineNumber = originatingFrame.frame.GetFileLineNumber() - 1;
        _determinedOriginatingFrame = fileName != null && File.Exists(fileName);
    }

    private string? GetCodePart()
    {
        if (!_determinedOriginatingFrame) return null;

        string? codePart = null;
        var codeLines = string.Join("\n", File.ReadAllLines(FileName!).Skip(LineNumber).ToArray());

        var indexOf = codeLines.IndexOf(_shouldMethod!, StringComparison.Ordinal);
        if (indexOf > 0)
        {
            codePart = codeLines[..(indexOf - 1)].Trim();

            // When the static method is used instead of the extension method,
            // the code part will be "Should".
            // Using EndsWith to cater for being inside a lambda
            if (codePart.EndsWith("Should", StringComparison.Ordinal))
            {
                codePart = GetCodePartFromParameter(indexOf, codeLines, codePart);
            }
            else
            {
                codePart = codePart.RemoveVariableAssignment().RemoveBlock();
            }
        }

        return codePart;
    }

    private string GetCodePartFromParameter(int indexOfMethod, string codeLines, string codePart)
    {
        var indexOfParameters =
            indexOfMethod +
            _shouldMethod!.Length;

        var parameterString = codeLines[indexOfParameters..];
        // Remove generic parameter if need be
        parameterString = parameterString.StartsWith("<", StringComparison.Ordinal)
            ? parameterString[(parameterString.IndexOf(">", StringComparison.Ordinal) + 2)..]
            : parameterString[1..];

        var parentheses = new Dictionary<char, char>
        {
            { '{', '}' },
            { '(', ')' },
            { '[', ']' }
        };

        var openParentheses = new List<char>();

        var found = false;
        var i = 0;
        while (!found && parameterString.Length > i)
        {
            var currentChar = parameterString[i];

            if (openParentheses.Count == 0 && currentChar is ',' or ')')
            {
                found = true;
                continue;
            }

            if (parentheses.TryGetValue(currentChar, out var parenthesis))
            {
                openParentheses.Add(parenthesis);
            }
            else if (openParentheses.Count > 0 && openParentheses.Last() == currentChar)
            {
                openParentheses.RemoveAt(openParentheses.Count - 1);
            }

            i++;
        }

        if (found)
        {
            codePart = parameterString[..i];
        }

        return codePart
            .StripLambdaExpressionSyntax()
            .CollapseWhitespace()
            .RemoveBlock()
            .Trim();
    }
}
