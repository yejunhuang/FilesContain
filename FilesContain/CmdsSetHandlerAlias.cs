using System.CommandLine;
using System.CommandLine.Invocation;

namespace CmdsNameSpace;
public static class CommandExtensions
{
    public static Command SetHandler(this Command cmd, ICommandHandler h)
    {
        cmd.Handler = h;
        return cmd;
    }
    public static Command SetAlias(this Command cmd, string alias)
    {
        cmd.AddAlias(alias);
        return cmd;
    }
}
public static class OptionExtensions
{
    public static Option SetAlias(this Option opt, string alias)
    {
        opt.AddAlias(alias);
        return opt;
    }
}
