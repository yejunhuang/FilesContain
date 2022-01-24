using System.CommandLine;
using System.CommandLine.NamingConventionBinder;

using static System.Environment;

using CmdsNameSpace;


// See https://aka.ms/new-console-template for more information

var longSynopsisText =
    $"在一个文件系统中，如何找到并删除所有多余文件夹而文件不丢失？{NewLine}" +
    $"两个文件二进制相同，指两个文件可能文件名、创建修改时间不同，但内容每一bit相同。{NewLine}" +
    $"如果一个文件夹A的任何文件，在另一个文件夹B中都能找到二进制相同文件，那么我们定义一种关系，称B包含A。{NewLine}" +
    $"如果B是A的父文件夹，显然B包含A。{NewLine}" +
    $"如果B不是A的父文件夹，B仍包含A，那么我们定义此种关系为非父包含。{NewLine}" +
    $"这个工具帮助找出一个文件系统中所有非父包含的文件夹。{NewLine}" +
    $"在一个文件系统中，删除所有非父包含文件夹，相当于删除多余二进制相同文件。{NewLine}" +
    $"{NewLine}";

//$"find . -type f -print0 | xargs -0 -L 1 -I f sudo sha256sum f | tee ~/candel/t.txt{NewLine}" +

var rootCommand = new RootCommand(longSynopsisText)
    {
        new Option<FileInfo>
        (
            new string[] { "--input-md5-filesname", "-i" },
            "input md5 files-name file"
        ).ExistingOnly(),
        new Argument<FileInfo>
        (
            "argument",
            "filecontain folder"
        ).ExistingOnly(),

        new Command("genfilesname", "gen all files name from a folder")
        {
            new Option<FileInfo>
            (
                new string[] {"--folder","-f" },
                ()=>new FileInfo("."),
                "folder name"
            ).ExistingOnly(),
            new Option<FileInfo>
            (
                new string[] { "--output-file", "-o" },
                ()=>new FileInfo(Path.Join(CurrentDirectory,"files_name_output.txt")),
                "output file name"
            ).LegalFileNamesOnly(),
        }.SetAlias("gfn").SetHandler(CommandHandler.Create(Cmds.CmdGenFilesName)),

        new Command("cmpfilesname", "compare two files name list from same folder")
        {
            new Option<FileInfo>
            (
                new string[] {"--folder-from","-f" },
                "folder from"
            ).ExistingOnly(),
            new Option<FileInfo>
            (
                new string[] { "--folder-to", "-t" },
                "folder to"
            ).ExistingOnly(),
        }.SetAlias("cfn").SetHandler(CommandHandler.Create(Cmds.CmdCmpFilesName)),

        new Command("md5filesname", "gen md5 from files name")
        {
            new Option<FileInfo>
            (
                new string[] {"--input-names-file","-i" },
                "input files-name file"
            ).ExistingOnly(),
            new Option<FileInfo>
            (
                new string[] { "--output-file", "-o" },
                ()=>new FileInfo(Path.Join(CurrentDirectory,"files_name_output.txt")),
                "output file name"
            ).LegalFileNamesOnly(),
        }.SetAlias("md5").SetHandler(CommandHandler.Create(Cmds.CmdMd5FilesName)),

        new Command("grouphash", "group file hash")
        {
            new Option<FileInfo[]>
            (
                new string[] {"--in-hash-files","-i" },
                "input hash(md5) files"
            ).ExistingOnly(),
            new Option<FileInfo>
            (
                new string[] { "--out-file", "-o" },
                ()=>new FileInfo(Path.Join(CurrentDirectory,"group_hash_output.txt")),
                "output file name"
            ).LegalFileNamesOnly(),
        }.SetAlias("gh").SetHandler(CommandHandler.Create(Cmds.CmdGroupHash)),

        new Command("diffhash", "diff two hash list")
        {
            new Option<FileInfo>
            (
                new string[] {"--minuend","-m" },
                "minuend hash(md5) file"
            ).ExistingOnly(),
            new Option<FileInfo>
            (
                new string[] {"--subtrahend","-s" },
                "subtrahend hash(md5) file"
            ).ExistingOnly(),
            new Option<FileInfo>
            (
                new string[] { "--out-file", "-o" },
                ()=>new FileInfo(Path.Join(CurrentDirectory,"diffhash_out.txt")),
                "output file name"
            ),
        }.SetAlias("dh").SetHandler(CommandHandler.Create(Cmds.CmdDiffHash)),

        new Command("filecontain", "file contain")
        {
            new Option<FileInfo[]>
            (
                new string[] {"--in-hash-files","-i" },
                "input hash(md5) files"
            ).ExistingOnly(),
            new Option<FileInfo>
            (
                new string[] { "--out-file", "-o" },
                ()=>new FileInfo(Path.Join(CurrentDirectory,"filecontain.txt")),
                "output file name"
            ),
        }.SetAlias("fc").SetHandler(CommandHandler.Create(Cmds.CmdFileContain)),

    }.SetHandler(CommandHandler.Create(Cmds.CmdRootRun));


var GlobalOption = new Option<FileInfo>(
    new string[] { "--config", "-c" },
    () =>
    {
        string? ProcPath = AppContext.BaseDirectory;
        //Console.WriteLine($"AppContext.BaseDirectory: {ProcPath}");
        string s = ProcPath + "config.json";
        //Console.WriteLine($"config file path: {s}");
        return new FileInfo(s);
    },
    "config filename"
    );
rootCommand.AddGlobalOption(GlobalOption);

return rootCommand.InvokeAsync(args).Result;



