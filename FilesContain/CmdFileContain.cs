
using System.Collections.Generic;
using ShellProgressBar;

namespace CmdsNameSpace;

public static partial class Cmds
{
    /// <summary>
    /// 算法：
    ///     1、读取一个或多个输入文件到List<HashFileNameRec>，按Hash升序排序。
    ///     2、按Hash分组，每组记录下在List<HashFileNameRec>中的开始位置Start、数量Count，
    ///         得到List<StartCountRec>。
    ///     3、由List<HashFileNameRec>构造DictTree。
    ///     4、由DictTree转换为可多线程查询的ImmuTreeList。
    ///     5、构建HashParents。每个对应文件名数量大于等于2的Hash，都有一个所有文件名的父节点集合。
    ///     6、构造潜在非父包含节点集--最上层Atleast2节点集(除根)，
    ///         每个节点仅由Hash数量最少为2的叶子节点自下而上构成，
    ///         且其中无一节点为另一节点的父节点。
    ///         
    ///     (将删除)6、从ImmuTree一级子节点开始，如某节点包含的Hash全在Atleast2List中，则记录下该节点(加到潜在节点集中)；
    ///         否则对子节点递归，递归函数有一布尔值记录父节点与子节点Hash数目是否相同，如相同直接递归到再下级子节点；
    ///         最后，得到一个潜在非父包含节点集，其中无一节点为另一节点的父节点。
    ///     7、遍历潜在非父包含节点集，按步骤7.1方法判断是否为非父包含。
    ///         如某节点不为非父包含，则递归该节点子节点判断是否为非父包含。
    ///         
    ///     7.1、判断某节点是否为非父包含。
    ///             首先，找出该节点所有Hash；
    ///                 每个Hash在HashParents中找到Parents集合；
    ///             计算每个Hash的Parents并集；
    ///             并减去该节点到根的中途节点；
    ///             最后如非空则得到1个非父包含。
    ///         
    /// </summary>
    /// <param name="inHashFiles"></param>
    /// <param name="outFile"></param>
    /// <param name="config"></param>
    public static async void CmdFileContain(List<FileInfo> inHashFiles, FileInfo outFile, FileInfo config)
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

        //var DictTree = DictTreeFromHashFileNameList(HashFileNameList);
        var DictTree = DictTreeFromStartCountList(StartCountList, HashFileNameList);
        Console.WriteLine("DictTree done.");

        var (ListTree, IndexList) = DictTreeToListTree(DictTree, HashFileNameList, StartCountList).Result;
        Console.WriteLine($"DictTreeToImmutableTree done. count={ListTree.Count}");

        var HashParents = MakeHashParents(StartCountList, ListTree, IndexList);
        var MostTopAtleast2 = MakeMostTopAtleast2(HashParents, StartCountList, ListTree, IndexList).Result;

        //var ImmuGroupList = MakeImmuGroup(ImmuTreeList, StartCountList, IndexList);
        //Console.WriteLine($"MakeImmuGroup done. count={ImmuGroupList.Count}");

        Console.WriteLine("NotParentContain begin...");
        NotParentContain(HashParents, MostTopAtleast2, StartCountList, HashFileNameList, ListTree, IndexList, outFile);
        Console.WriteLine("NotParentContain done.");
    }
    private static string FileNameConvert(string s, int i)
    {
        if ('.' == s[0]) s = s.Substring(1);
        return $"/{i}{s}";
    }



    /// <summary>
    /// 遍历潜在非父包含节点集，如某节点不为非父包含，则递归该节点子节点(排除叶子节点)判断是否为非父包含。
    /// 
    /// 判断某潜在节点是否为非父包含：
    ///     首先，找出该节点所有Hash；
    ///         每个Hash在HashParents中找到Parents集合；
    ///     计算每个Hash的Parents并集；
    ///     并减去该节点到根的中途节点；
    ///     最后如非空则得到1个非父包含。
    ///     排序，如列表非空则循环输出列表首节点并删除其所有父至根节点。
    /// </summary>
    /// <param name="hashParents"></param>
    /// <param name="mostTopAtleast2"></param>
    /// <param name="startCountList"></param>
    /// <param name="hashFileNameList"></param>
    /// <param name="immuTreeList"></param>
    /// <param name="hashFileNameListToImmuListIndexList"></param>
    /// <param name="outFile"></param>
    private static void NotParentContain(
        List<HashParents> hashParents,
        List<int> mostTopAtleast2,
        List<StartCountRec> startCountList,
        List<HashFileNameRec> hashFileNameList,
        List<ListTreeNode> immuTreeList,
        List<int> hashFileNameListToImmuListIndexList,
        FileInfo outFile)
    {
        using var OutWriter = File.CreateText(outFile.FullName);

        using ProgressBar progressBar = new ProgressBar(mostTopAtleast2.Count, "NotParentContain progress");
        Parallel.ForEach(mostTopAtleast2, (mostTopNode) =>
        {
            //判断一个潜在的非父包含节点
            var NotParentContainList = RecursionNotParentContainNode(mostTopNode, hashParents, startCountList, immuTreeList);
            lock (progressBar)
            {
                progressBar.Tick();
            }
        });

    }
    private static List<(int, List<int>)> RecursionNotParentContainNode(
        int node,
        List<HashParents> hashParents,
        List<StartCountRec> startCountList,
        List<ListTreeNode> immuTreeList
        )
    {
        //排除叶子节点
        if (immuTreeList[node].Childs == null)
        {
            return new List<(int, List<int>)>(0);
        }

        //当前节点
        var NotParentContainList = NotParentContainNode(node, startCountList, hashParents, immuTreeList);
        if (NotParentContainList.Count > 0)
        {
            return new List<(int, List<int>)>() { (node, NotParentContainList) };
        }

        //递归子节点
        var Result = new List<(int, List<int>)>();
        foreach (var item in immuTreeList[node].Childs)
        {
            Result.AddRange(RecursionNotParentContainNode(item.Value, hashParents, startCountList, immuTreeList));
        }
        return Result;
    }

    /// <summary>
    /// 判断某潜在节点是否为非父包含：
    ///     首先，找出该节点所有Hash；
    ///         每个Hash在HashParents中找到Parents集合；
    ///     计算每个Hash的Parents并集；
    ///     并减去该节点到根的中途节点；
    ///     最后如非空则得到1个非父包含。
    ///     排序，如列表非空则循环输出列表首节点并删除其所有父至根节点。
    /// </summary>
    /// <param name="node"></param>
    /// <param name="startCountList"></param>
    /// <param name="hashParents"></param>
    /// <param name="listTree"></param>
    /// <returns>如List.Count为0则非非父包含</returns>
    private static List<int> NotParentContainNode(
        int node,
        List<StartCountRec> startCountList,
        List<HashParents> hashParents,
        List<ListTreeNode> listTree)
    {
        var ResultSet = new List<int>();
        //var Hashs = listTree[node].AtLeast2StartCountIdxList.Select(i ==> startCountList[i])
        var Hashs = from Idx in listTree[node].HashParentIdxList select hashParents[Idx].ParentIdxList;
        foreach (var Hash in Hashs) ResultSet.SetUnion(Hash);
        var NodeToRootList = NodeToRoot(node, listTree);
        ResultSet.SetSub(NodeToRootList);

        var Result = new List<int>();
        while (ResultSet.Count > 0)
        {
            Result.Add(ResultSet[0]);
            ResultSet.SetSub(NodeToRoot(ResultSet[0], listTree));
        }
        return Result;
    }

    private static void NotParentContain2(
        List<SameHashAtleast2ImmuGroup> groupList,
        List<StartCountRec> startCountList,
        List<HashFileNameRec> hashFileNameList,
        List<ListTreeNode> immuTreeList,
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
                    foreach (var hash in CurNode.AtLeast2SameChildsHashes)
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
                            IntersectionList = IntersectionList.SetSub(LeafToExclusiveRootList);
                            //只要交集中有一项减去LeafToExclusiveRootList，全部交集也减去LeafToExclusiveRootList
                        }
                        else
                        {
                            IntersectionList = IntersectionList.SetIntersect(GroupImmuTreeIdxList);
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
