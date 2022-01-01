using System.Runtime.CompilerServices;

namespace CmdsNameSpace;

/// <summary>
/// true转为ErrorString(null)转为true；
/// false转为ErrorString("")转为false;
/// null转为ErrorString(null)转为true；
/// ""或"any"转为ErrorString("any")转为false；
/// </summary>
public class ErrorString
{
    private string? S;
    public ErrorString(string? s)
    {
        this.S = s;
    }
    public bool Success() => this.S is null;

    public override string ToString() { return this.S ?? "null"; }
    public static implicit operator bool(ErrorString s) => s.Success();
    public static implicit operator ErrorString(string s) => new ErrorString(s);
    public static implicit operator ErrorString(bool b) => new ErrorString(b ? null : "");
}

/// <summary>
/// 在需要异常地方，throw EX.New()。
/// 抛出的异常消息自带调用者所属源码文件、行数、成员名。
/// </summary>
public class EX : Exception
{
    public EX(string message) : base(message) { }
    public static EX New(
        [CallerFilePath] string? callerFilePath = null,
        [CallerLineNumber] int? callerLineNumber = null,
        [CallerMemberName] string? callerMemberName = null
        )
    {
        return new EX($@"CallerMemberName: {callerMemberName}, CallerLineNumber: {callerLineNumber}, CallerFilePath: {callerFilePath}");
    }
}