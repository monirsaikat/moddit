namespace Modit;

/// <summary>
/// Stable error codes. They cross the engine IPC boundary as integers, drive CLI exit codes and
/// localized messages, and are documented in docs/errors.md. Never renumber an existing value.
/// </summary>
public enum ErrorCode
{
    None = 0,

    // 1xx: generic
    Unknown = 100,
    Cancelled = 101,
    InvalidArgument = 102,
    NotSupported = 103,

    // 2xx: input/output
    FileNotFound = 200,
    FileAccessDenied = 201,
    FileTooLarge = 202,
    OutputWriteFailed = 203,

    // 3xx: PDF
    PdfCorrupt = 300,
    PdfEncryptedNeedsPassword = 301,
    PdfWrongPassword = 302,
    PdfUnsupportedSecurity = 303,
    PdfPageOutOfRange = 304,
    PdfStructureOperationFailed = 305,

    // 4xx: engine hosts
    EngineUnavailable = 400,
    EngineCrashed = 401,
    EngineMemoryLimit = 402,
    EngineTimeout = 403,
    EnginePackMissing = 404,

    // 5xx: enterprise
    PolicyBlocked = 500,
    Unlicensed = 501,

    // 6xx: install/update
    UpdateFailed = 600,
    UpdateSignatureInvalid = 601,

    // 7xx: conversion
    ConvertUnsupportedFormat = 700,
    ConvertInputCorrupt = 701,
    ConvertCodecMissing = 702,
    ConvertFailed = 703,
}

/// <summary>CLI process exit codes (docs/plan/05, "CLI mapping").</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Partial = 1;
    public const int InvalidArguments = 2;
    public const int PolicyBlocked = 3;
    public const int EnginePackMissing = 4;
    public const int OperationError = 5;
    public const int Unlicensed = 6;
    public const int Cancelled = 7;
    public const int UpdateError = 8;

    public static int FromError(ErrorCode code) => code switch
    {
        ErrorCode.None => Ok,
        ErrorCode.InvalidArgument => InvalidArguments,
        ErrorCode.PolicyBlocked => PolicyBlocked,
        ErrorCode.EnginePackMissing => EnginePackMissing,
        ErrorCode.Unlicensed => Unlicensed,
        ErrorCode.Cancelled => Cancelled,
        ErrorCode.UpdateFailed or ErrorCode.UpdateSignatureInvalid => UpdateError,
        _ => OperationError,
    };
}
