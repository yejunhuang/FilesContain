using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.IO.Enumeration;

using static System.Environment;
using ShellProgressBar;
using System.CommandLine.Parsing;
using System.CommandLine.IO;

namespace CmdsNameSpace;

public static partial class Cmds
{
    public static void CmdRootRun(FileInfo inputMd5Filesname, FileInfo argument, FileInfo config)
    {
        //console.Out.WriteLine($"{parseResult}");
        Console.WriteLine($"root run, inputNamesFile is {inputMd5Filesname}, argument is {argument}");
        //Console.ReadLine();
    }

    public static void CmdGenFilesName(FileInfo folder, FileInfo outputFile, FileInfo config)
    {
        //Console.WriteLine($"CmdGenFilesName run. config is {config}, folder is {folder}, outputFile is {outputFile}");
        //Console.WriteLine();
        //Console.ReadLine();
        //var Files = Directory.GetFiles(folder.FullName, "", SearchOption.AllDirectories);
        //Console.WriteLine(String.Join(NewLine, Files));

        //Console.WriteLine($"folder.Name={folder.FullName}");
        //return;


        var E = new FileSystemEnumerable<string>
            (
                directory: (folder.Name ==".")?"." : folder.FullName,
                transform: (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath(),
                options: new EnumerationOptions() { RecurseSubdirectories = true, AttributesToSkip = 0 }
            )
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
        };

        List<string> L = new List<string>();
        foreach (var F in E)
        {
            if (outputFile.Name == "-")
            {
                Console.Out.WriteLine(F);
            }
            else
            {
                L.Add(F);
            }
        }

        if(outputFile.Name == "-")
        {
        }
        else
        {
            File.WriteAllLines(outputFile.FullName, L);
        }
    }

    public static async Task CmdMd5FilesName(FileInfo inputNamesFile, FileInfo outputFile, FileInfo config)
    {
        Console.WriteLine($"CmdMd5FilesName run. from {inputNamesFile.Name} to {outputFile.Name}");
        var FileNames = File.ReadAllLines(inputNamesFile.FullName);

        List<string> OutputList = new List<string>();
        using ProgressBar progressBar = new ProgressBar(FileNames.Length, "md5 progress");
        await Parallel.ForEachAsync
        (
            FileNames,
            async (FileName, Token) =>
            {
                MD5 M = MD5.Create();
                using (var FS = new FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
                {
                    var HashBytes = await M.ComputeHashAsync(FS, Token);
                    string Hex = Convert.ToHexString(HashBytes);
                    lock (OutputList)
                    {
                        OutputList.Add($"{Hex} {FileName}");
                        progressBar.Tick();
                    }
                }
            }
        );

        Console.WriteLine("write output list...");
        await File.WriteAllLinesAsync(outputFile.FullName, OutputList);
        Console.WriteLine("CmdMd5FilesName done.");
    }

    public static void CmdCmpFilesName(FileInfo hashFileFrom, string filterFrom, FileInfo hashFileTo, string filterTo, FileInfo outFile, FileInfo config)
    {
        ErrorString ErrStr;
        //(ErrStr, var RecFrom) = LoadHashFile(hashFileFrom, (s) => ('.' == s[0])? s.Substring(1) : s);
        (ErrStr, var RecFromList) = LoadFilterHashFile(hashFileFrom, filterFrom);
        if (!ErrStr)
        {
            Console.WriteLine($"CmdCmpFilesName()->{ErrStr}");
            return;
        }
        (ErrStr, var RecToList) = LoadFilterHashFile(hashFileTo, filterTo);
        if (!ErrStr)
        {
            Console.WriteLine($"CmdCmpFilesName()->{ErrStr}");
            return;
        }

        var Comparer = Comparer<HashFileNameRec>.Create(CmpFileName);
        var FromSubTo = SetQuickSub(RecFromList, RecToList, Comparer);
        var ToSubFrom = SetQuickSub(RecToList, RecFromList, Comparer);
        var Intersect = SetIntersect(RecFromList, RecToList, Comparer);

        using var OutWriter = File.CreateText(outFile.FullName);
        OutWriter.WriteLine($"CmdCmpFilesName run. from {hashFileFrom} filter {filterFrom} to {hashFileTo} filter {filterTo}");
        OutWriter.WriteLine("FromSubTo-----------------------------------");
        foreach (var item in FromSubTo)
        {
            OutWriter.WriteLine("  " + item.filename);
        }
        OutWriter.WriteLine("ToSubFrom-----------------------------------");
        foreach (var item in ToSubFrom)
        {
            OutWriter.WriteLine("  " + item.filename);
        }
        OutWriter.WriteLine("Intersect diff hash-----------------------------------");
        var DiffHashIntersectList = new List<string>();
        foreach (var Rec in Intersect)
        {
            int IdxFrom = RecFromList.BinarySearch(Rec, Comparer);
            if (IdxFrom < 0) throw new ApplicationException("RecFromList.BinarySearch() < 0");
            int IdxTo = RecToList.BinarySearch(Rec, Comparer);
            if (IdxTo < 0) throw new ApplicationException("RecToList.BinarySearch() < 0");
            if (RecFromList[IdxFrom].CompareTo(RecToList[IdxTo]) != 0)
                DiffHashIntersectList.Add(Rec.filename);
        }
        foreach (var Item in DiffHashIntersectList)
        {
            OutWriter.WriteLine("  " + Item);
        }
    }

    static int CmpFileName(HashFileNameRec a, HashFileNameRec b)
    {
        return a.filename.CompareTo(b.filename);
    }

    /// <summary>
    /// 返回按Rec.filename排序的Rec列表
    /// </summary>
    /// <param name="f"></param>
    /// <param name="filter"></param>
    /// <returns></returns>
    private static (ErrorString, List<HashFileNameRec>) LoadFilterHashFile(FileInfo f, string filter)
    {
        ErrorString ErrStr;
        (ErrStr, var RecList) = LoadHashFile(f, (s) => ('.' == s[0]) ? s[1..] : s);
        if (!ErrStr) return (ErrStr, RecList);
        if (null != filter)
        {
            RecList = (from Rec in RecList where Rec.filename.StartsWith(filter)
                       select new HashFileNameRec(Rec.hash, Rec.filename[filter.Length..])).ToList();
        }
        RecList.Sort(CmpFileName);
        return (true, RecList);
    }
}

