
using System.Collections.Generic;
using ShellProgressBar;

namespace CmdsNameSpace;

public static partial class Cmds
{
    public static void CmdFileContain(List<FileInfo> inHashFiles, FileInfo outFile, FileInfo config)
    {
        Console.WriteLine($"CmdFileContain, output file {outFile.FullName}");
        List<HashFileNameRec> HashFileNameList = new List<HashFileNameRec>();
        for (int i = 0; i < inHashFiles.Count; i++)
        {
            Console.WriteLine($"input file {i} {inHashFiles[i].FullName}");

            ErrorString ErrStr;
            (ErrStr, var TempList) = LoadHashFile(inHashFiles[i], (s) => FileNameConvert(s, i));
            if (!ErrStr)
            {
                Console.WriteLine($"CmdFileContain()->{ErrStr}");
                return;
            }
            HashFileNameList.AddRange(TempList);
        }
        Console.WriteLine($"LoadHashFile done. count={HashFileNameList.Count}");
        HashFileNameList.Sort();
        Console.WriteLine("sort done.");

        var StartCountList = MakeStartCountList(HashFileNameList);
        Console.WriteLine($"MakeStartCountList done. count={StartCountList.Count}");

        var DictTree = DictTreeFromHashFileNameList(HashFileNameList);
        Console.WriteLine("DictTree done.");

        var (ImmuTreeList, IndexList) = DictTreeToImmutableTree(DictTree, HashFileNameList).Result;
        Console.WriteLine($"DictTreeToImmutableTree done. count={ImmuTreeList.Count}");

        var ImmuGroupList = MakeImmuGroup(ImmuTreeList, StartCountList, IndexList);
        Console.WriteLine($"MakeImmuGroup done. count={ImmuGroupList.Count}");

        Console.WriteLine("NotParentContain begin...");
        NotParentContain(ImmuGroupList, StartCountList, HashFileNameList, ImmuTreeList, IndexList, outFile);
        Console.WriteLine("NotParentContain done.");
    }
    private static string FileNameConvert(string s, int i)
    {
        if ('.' == s[0]) s = s.Substring(1);
        return $"/{i}{s}";
    }


    private static void NotParentContain(
        List<SameHashAtleast2ImmuGroup> groupList,
        List<StartCountRec> startCountList,
        List<HashFileNameRec> hashFileNameList,
        List<ImmutableTreeNode> immuTreeList,
        List<int> indexList,
        FileInfo outFile)
    {
        using var OutWriter = File.CreateText(outFile.FullName);
        //outFile
        //在group中找大于1的分组
        List<StartCountRec> AtLeast2List = startCountList.Where((r) => r.Count > 1).ToList();
        Console.WriteLine($"At least 2 Group count={AtLeast2List.Count}.");

        using ProgressBar progressBar = new ProgressBar(AtLeast2List.Count, "NotParentContain progress");
        Parallel.ForEach(AtLeast2List, (hashStartCount) =>
        {
            //对每一组做

            var FindRec = new SameHashAtleast2ImmuGroup(new byte[0], new List<int>(0));

            //对组内所有HashFileNameItem做
            int HashFileNameIdx = hashStartCount.Start;
            for (int i = 0; i < hashStartCount.Count; i++)
            {
                //对一个HashFileNameItem做
                //HashFileNameIdx+i

                //从叶子节点往上直到根，对每个节点做
                //var LeafToExclusiveRootList = NodeToExclusiveRoot(indexList[HashFileNameIdx + i], immuTreeList);
                var LeafToExclusiveRootList = ExclusiveNodeToExclusiveRoot(indexList[HashFileNameIdx + i], immuTreeList);
                //int ImmuRootIndex = immuTreeList.Count - 1;
                foreach (var CurNodeIdx in LeafToExclusiveRootList)
                {
                    //当前节点
                    var CurNode = immuTreeList[CurNodeIdx];

                    //对当前节点包含Hash集的每项做

                    //建立交集
                    List<int> IntersectionList = new List<int>();
                    bool bFirstInitIntersectionList = true;
                    bool bFindSigleHashFile = false;
                    foreach (var hash in CurNode.ChildsHashes)
                    {
                        //查所属group
                        FindRec.Hash = hash;
                        var GroupIdx = groupList.BinarySearch(FindRec);
                        if (GroupIdx < 0)
                        {
                            //不在AtLeast2里

                            //应在StarCountRecList里
                            //测试，以下两行可注释
                            //int BinarySearchResult = startCountList.BinarySearch(new StartCountRec(hash, 0, 0));
                            //if (BinarySearchResult < 0) throw EX.New();

                            bFindSigleHashFile = true; //包含一个单一hash的元素，不可能非父包含
                            break;
                        }

                        var GroupImmuTreeIdxList = groupList[GroupIdx].ImmuTreeIdxList;
                        //Group.

                        //建立交集
                        if (bFirstInitIntersectionList)
                        {
                            bFirstInitIntersectionList = false;
                            IntersectionList = GroupImmuTreeIdxList;
                            //去除从叶子节点到根的节点
                            IntersectionList = IntersectionList.Sub(LeafToExclusiveRootList);
                            //只要交集中有一项减去LeafToExclusiveRootList，全部交集也减去LeafToExclusiveRootList
                        }
                        else
                        {
                            IntersectionList = IntersectionList.SortedIntersect(GroupImmuTreeIdxList);
                        }
                        if (IntersectionList.Count == 0) break;
                    }
                    if (!bFindSigleHashFile)
                    {
                        //如交集非空，交集节点集即非父包含当前节点
                        if (IntersectionList.Count > 0)
                        {
                            //优化IntersectionList，去除重复父目录
                            IntersectionList = RemoveImmuParent(IntersectionList, immuTreeList);

                            //输出IntersectionList包含CurNode
                            lock (OutWriter)
                            {
                                OutWriter.WriteLine($"{CurNode.PathFromRoot}");
                                foreach (var item in IntersectionList)
                                {
                                    OutWriter.WriteLine($"  {immuTreeList[item].PathFromRoot}");
                                }
                                OutWriter.WriteLine();
                            }
                        }
                    }
                }

            }
            lock (progressBar)
            {
                progressBar.Tick();
            }
        });

    }
}
