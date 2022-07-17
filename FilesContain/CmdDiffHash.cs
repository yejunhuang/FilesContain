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

    //Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript.EvaluateAsync
    //https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp.Scripting/
    //https://itnext.io/getting-start-with-roslyn-c-scripting-api-d2ea10338d2b

    //https://riptutorial.com/roslyn-scripting/learn/100006/evaluate-a-script-with-parameters
    //var result = await CSharpScript.EvaluateAsync<int>("X*2 + Y*2", globals: point);

    //var discountFilter = "album => album.Quantity > 0";
    //var options = ScriptOptions.Default.AddReferences(typeof(Album).Assembly);
    //Func<Album, bool> discountFilterExpression = await CSharpScript.EvaluateAsync<Func<Album, bool>>(discountFilter, options);
}