namespace Modit;

/// <summary>Base type for every failure Modit raises on purpose. Always carries a stable code.</summary>
public class ModitException : Exception
{
    public ModitException()
        : this(ErrorCode.Unknown, "An unknown error occurred.")
    {
    }

    public ModitException(string message)
        : this(ErrorCode.Unknown, message)
    {
    }

    public ModitException(string message, Exception innerException)
        : this(ErrorCode.Unknown, message, innerException)
    {
    }

    public ModitException(ErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public ErrorCode Code { get; }
}

/// <summary>The engine host process failed, crashed or could not be started.</summary>
public sealed class EngineException : ModitException
{
    public EngineException()
    {
    }

    public EngineException(string message)
        : base(ErrorCode.EngineUnavailable, message)
    {
    }

    public EngineException(string message, Exception innerException)
        : base(ErrorCode.EngineUnavailable, message, innerException)
    {
    }

    public EngineException(ErrorCode code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}
