using WireLink.Core.Communication;

namespace WireLink.Tests;

internal sealed class RecordingProtocolTrace : IProtocolTrace
{
    public List<string> DebugMessages { get; } = [];
    public List<string> InformationMessages { get; } = [];
    public List<string> WarningMessages { get; } = [];
    public List<(string Message, Exception? Exception)> ErrorMessages { get; } = [];

    public void Debug(string message) => DebugMessages.Add(message);
    public void Information(string message) => InformationMessages.Add(message);
    public void Warning(string message) => WarningMessages.Add(message);
    public void Error(string message, Exception? exception = null) => ErrorMessages.Add((message, exception));
}
