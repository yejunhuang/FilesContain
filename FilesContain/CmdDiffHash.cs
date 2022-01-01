using static System.Environment;

namespace CmdsNameSpace;
public static partial class Cmds
{
    public static void CmdDiffHash(FileInfo minuend, FileInfo subtrahend, FileInfo outFile, FileInfo config)
    {
        Console.WriteLine($"CmdDiffHash, minuend {minuend.FullName}, subtrahend {subtrahend.FullName}, output file {outFile.FullName}");
        ErrorString ErrStr;
        (ErrStr, var MinuendList) = LoadHashFile(minuend);
        if (!ErrStr) throw new Exception($"CmdDiffHash() LoadFile Minuend->{ErrStr}");
        (ErrStr, var SubtrahendList) = LoadHashFile(subtrahend);
        if (!ErrStr) throw new Exception($"CmdDiffHash() LoadFile Subtrahend->{ErrStr}");
        Console.WriteLine("Minuend Subtrahend loaded...");

        MinuendList.Sort();
        SubtrahendList.Sort();
        Console.WriteLine("Sorted.");

        var MinuendGroups = MakeStartCountList(MinuendList);
        Console.WriteLine("MinuendList Hash Group builded.");

        var SubtrahendGroups = MakeStartCountList(SubtrahendList);
        Console.WriteLine("SubtrahendList Hash Group builded.");

    }


}