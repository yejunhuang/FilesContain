using static System.Environment;

namespace CmdsNameSpace;
public static partial class Cmds
{
    public static void CmdGroupHash(List<FileInfo>  inHashFiles, FileInfo outFile, FileInfo config)
    {
        Console.WriteLine($"Input hash files is:{NewLine}");
        foreach (var item in inHashFiles)
        {
            Console.WriteLine(item.Name);
        }
    }

}