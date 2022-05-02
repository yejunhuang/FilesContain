
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
    ///     6、构造潜在非父包含节点集--最上层纯2节点集(除根)，
    ///         每个节点仅由Hash数量最少为2的叶子节点自下而上构成，
    ///         且其中无一节点为另一节点的父节点。
    ///         
    ///         纯2节点定义：如某节点为叶子节点，则其相同Hash数量最少为2；如某节点非叶子节点，则其子节点均为纯2节点。
    ///         非2节点定义：不是纯2节点。如某节点为叶子节点，则其相同Hash数量为1；如某节点非叶子节点，则其子节点至少含有1个非2节点。
    /// 
    ///     7、遍历最上层纯2节点集，按步骤7.1方法判断是否为非父包含。
    ///         如某节点不为非父包含，则递归该节点子节点判断是否为非父包含。
    ///         
    ///     7.1、判断某纯2节点是否为非父包含。
    ///             首先，找出该节点所有Hash；
    ///                 每个Hash在HashParents中找到Parents集合；
    ///             计算所有Hash的Parents交集；
    ///             并减去该节点到根的中途节点；
    ///             最后如非空则得到对该纯2节点的非父包含集，但此集合内元素可能有父子关系。
    ///             如非父包含集非空，则循环输出列表首节点、并删除其父节点至根节点。
    /// </summary>
    /// <param name="inHashFiles">输入HashFile列表</param>
    /// <param name="outFile">输出File</param>
    public static void CmdFileContain(List<FileInfo> inHashFiles, FileInfo outFile)
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

        var DictTree = DictTreeFromStartCountList(StartCountList, HashFileNameList);
        Console.WriteLine("DictTree done.");

        var (ListTree, IndexList) = DictTreeToListTree(DictTree, HashFileNameList, StartCountList).Result;
        Console.WriteLine($"DictTreeToListTree done. count={ListTree.Count}");

        var HashParents = MakeHashParents(StartCountList, ListTree, IndexList);
        Console.WriteLine($"MakeHashParents done. count={HashParents.Count}");

        var MostTopPure2 = MakeMostTopPure2(HashParents, StartCountList, ListTree, IndexList).Result;
        Console.WriteLine($"MakeMostTopPure2 done. count={MostTopPure2.Count}");

        Console.WriteLine("NotParentContain begin...");
        NotParentContain(HashParents, MostTopPure2, StartCountList, HashFileNameList, ListTree, IndexList, outFile);
        Console.WriteLine("NotParentContain done.");
    }

    /// <summary>
    /// 输入文件一行转换为HashFileNameRec时使用的文件名转换函数，
    /// 如文件名形如"./a/b/c"，把最前面的"."去掉；
    /// 把文件名按输入文件序号加上前缀序号，把第1个输入文件中某行文件名"/a/b/c"转换为"/1/a/b/c"，把第2个输入文件中某行文件名"/a/b/c"转换为"/2/a/b/c"。
    /// </summary>
    /// <param name="s"></param>
    /// <param name="i"></param>
    /// <returns></returns>
    private static string FileNameConvert(string s, int i)
    {
        if ('.' == s[0]) s = s.Substring(1);
        return $"/{i}{s}";
    }



    /// <summary>
    /// 遍历最上层纯2节点集，如某节点不为非父包含，则递归该节点子节点(排除叶子节点)判断是否为非父包含。
    /// 
    /// 判断某潜在节点是否为非父包含：
    ///     首先，找出该节点所有Hash；
    ///         每个Hash在HashParents中找到Parents集合；
    ///     计算所有Hash的Parents交集；
    ///     并减去该节点到根的中途节点；
    ///     最后如非空则得到对该纯2节点的非父包含集，但此集合内元素可能有父子关系。
    ///     如非父包含集非空，则循环输出列表首节点、并删除其父节点至根节点。
    /// </summary>
    /// <param name="hashParents"></param>
    /// <param name="mostTopPure2List"></param>
    /// <param name="startCountList"></param>
    /// <param name="hashFileNameList"></param>
    /// <param name="listTree"></param>
    /// <param name="hashFileNameListToListTreeIdxList"></param>
    /// <param name="outFile"></param>
    private static void NotParentContain(
        List<HashParents> hashParents,
        List<int> mostTopPure2List,
        List<StartCountRec> startCountList,
        List<HashFileNameRec> hashFileNameList,
        List<ListTreeNode> listTree,
        List<int> hashFileNameListToListTreeIdxList,
        FileInfo outFile)
    {
        using var OutWriter = File.CreateText(outFile.FullName);
        using ProgressBar progressBar = new ProgressBar(mostTopPure2List.Count, "NotParentContain progress");
        Parallel.ForEach(mostTopPure2List, (mostTopPure2Node) =>
        {
            //返回某纯2节点的最顶非父包含列表
            var NotParentContainList = RecursionMostTopNotParentContainNode(mostTopPure2Node, hashParents, startCountList, listTree);
            lock(OutWriter)
            {
                foreach (var nodeitem in NotParentContainList)
                {
                    OutWriter.WriteLine(listTree[nodeitem.node].PathFromRoot);
                    foreach (var listitem in nodeitem.list)
                    {
                        OutWriter.WriteLine("  " + listTree[listitem].PathFromRoot);
                    }
                }
            }
            lock (progressBar)
            {
                progressBar.Tick();
            }
        });
    }

    /// <summary>
    /// 递归调用，返回某纯2节点的最顶非父包含列表。
    /// 列表元素为元组(节点，非父包含集)。
    /// 
    /// 叶子节点返回空列表。
    /// if(某非叶子纯2节点为非父包含)
    /// {返回单元素列表，元素为元组(纯2节点，非父包含集)；}
    /// else
    /// {返回所有子节点的最顶非父包含列表的合并}
    /// 
    /// </summary>
    /// <param name="node"></param>
    /// <param name="hashParents"></param>
    /// <param name="startCountList"></param>
    /// <param name="listTree"></param>
    /// <returns></returns>
    private static List<(int node, List<int> list)> RecursionMostTopNotParentContainNode(
        int node,
        List<HashParents> hashParents,
        List<StartCountRec> startCountList,
        List<ListTreeNode> listTree
        )
    {
        if (null == listTree[node].Childs)
        {
            //叶子节点返回空列表
            return new List<(int, List<int>)>(0);
        }

        var NotParentContainList = NotParentContainNode(node, startCountList, hashParents, listTree);
        if (NotParentContainList.Count > 0)
        {
            //非叶子纯2节点为非父包含
            //返回单元素列表
            return new List<(int, List<int>)>() { (node, NotParentContainList) };
        }

        //返回所有子节点的最顶非父包含列表的合并
        //递归子节点
        var Result = new List<(int, List<int>)>();
        foreach (var item in listTree[node].Childs.EmptyIfNull())
        {
            Result.AddRange(RecursionMostTopNotParentContainNode(item.Value, hashParents, startCountList, listTree));
        }
        return Result;
    }

    /// <summary>
    /// 判断某纯2节点是否为非父包含：
    ///     首先，找出该节点所有Hash；
    ///         每个Hash在HashParents中找到Parents集合；
    ///     计算所有Hash的Parents交集；
    ///     并减去该节点到根的中途节点；
    ///     最后如非空则得到对该纯2节点的非父包含集，但此集合内元素可能有父子关系。
    ///     如非父包含集非空，则循环输出列表首节点、并删除其父节点至根节点。
    /// 
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
        var NotParentContainSet = new List<int>(0);
        //var Hashs = listTree[node].AtLeast2StartCountIdxList.Select(i ==> startCountList[i])
        var HashsParents = from Idx in listTree[node].HashParentIdxList select hashParents[Idx].ParentIdxList;
        bool IsFirst = true;
        foreach (var Hash in HashsParents)
        {
            if (IsFirst)
            {
                IsFirst = false;
                NotParentContainSet = Hash;
            }
            else
                NotParentContainSet = NotParentContainSet.SetIntersect(Hash);
        }
        var NodeToRootList = NodeToRoot(node, listTree);
        NotParentContainSet = NotParentContainSet.SetSub(NodeToRootList);

        var Result = new List<int>();
        while (NotParentContainSet.Count > 0)
        {
            Result.Add(NotParentContainSet[0]);
            NotParentContainSet=NotParentContainSet.SetSub(NodeToRoot(NotParentContainSet[0], listTree));
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
