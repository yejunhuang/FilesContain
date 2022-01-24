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

    public static void CmdCmpFilesName(FileInfo folderFrom, FileInfo folderTo, FileInfo config)
    {
        Console.WriteLine($"CmdCmpFilesName run. from {folderFrom.Name} to {folderTo.Name}");
        var F = File.ReadAllLines(folderFrom.FullName);
        var T = File.ReadAllLines(folderTo.FullName);
        var HF = new HashSet<string>(F);
        var HT = new HashSet<string>(T);

        var InFromNotInTo = (from From in F.AsParallel()
                             where !HT.Contains(From)
                             select From).ToList<string>();

        Console.WriteLine("InFromNotInTo---------------------------");
        Console.WriteLine(String.Join(NewLine, InFromNotInTo));

        var InToNotInFrom = (from To in T.AsParallel()
                             where !HF.Contains(To)
                             select To).ToList<string>();

        Console.WriteLine("InToNotInFrom---------------------------");
        Console.WriteLine(String.Join(NewLine, InToNotInFrom));

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

}

