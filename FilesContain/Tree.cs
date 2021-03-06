using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace CmdsNameSpace;

public static partial class Cmds
{
    /// <summary>
    /// 并发树节点，用于多线程生成树
    /// </summary>
    public class DictTreeNode
    {
        public string PathFromRoot; //从根节点到当前节点的路径，形如"/a/b/c"
        //public ConcurrentDictionary<string, ConcurrentTreeNode> Childs; //子节点，string为子节点路径，
        public Dictionary<string, DictTreeNode> Childs; //子节点，string为子节点路径，
        public ReaderWriterLockSlim ChildsLock;

        public DictTreeNode(string pathFromRoot)
        {
            this.PathFromRoot = pathFromRoot;
            this.Childs = new Dictionary<string, DictTreeNode>();
            this.ChildsLock = new ReaderWriterLockSlim();
        }

        //Childs.Count为0表示叶子节点，以下才有意义
        public int StartCountListIdx; //在List<StartCountRec>中的索引
        public int StartOffset; //StartCountRec中偏离Start的Offset
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="hash"></param>
    /// <param name="filename">形如 "/a/b/c"</param>
    public record HashFileNameRec(byte[] hash, string filename) : IComparable<HashFileNameRec>
    {
        public int CompareTo(HashFileNameRec? other)
        {
            if (other == null) throw EX.New();
            return CmpHashBytes(hash, other.hash);
        }
    }
    private static int CmpHashBytes(byte[] x, byte[] y)
    {
        if ((x == null) || (y == null) || (x.Length != y.Length))
            throw EX.New();
        for (int i = 0; i < x.Length; i++)
        {
            if (x[i] != y[i]) return x[i] - y[i];
        }
        return 0;

    }

    /// <summary>
    /// 把输入文件中一行变为HashFileNameRec。
    /// </summary>
    /// <param name="s">输入文件中一行</param>
    /// <param name="fileNameConvert">文件名转换函数</param>
    /// <returns>ErrorString或HashFileNameRec</returns>
    static (ErrorString, HashFileNameRec?) HashFileRecFromStr(string s, Func<string, string>? fileNameConvert = null)
    {
        int i = s.IndexOf(' ');
        if (i <= 0) return ("HashFileRecFromStr() s.IndexOf(' ') <= 0", default);
        var HashStr = s.Substring(0, i);
        var Hash = Convert.FromHexString(HashStr);
        var FileName = s.Substring(i + 1);
        FileName = FileName.Trim();
        return (true, new HashFileNameRec(Hash, (null == fileNameConvert) ? FileName : fileNameConvert(FileName)));
    }
    private static (ErrorString, List<HashFileNameRec>) LoadHashFile(FileInfo f, Func<string, string>? fileNameConvert = null)
    {
        var A = File.ReadAllLines(f.FullName);
        var L = new List<HashFileNameRec>();
        foreach (var item in A)
        {
            var (Err, Rec) = HashFileRecFromStr(item, fileNameConvert);
            if (!Err) return ($"LoadHashFile()->{Err}", L);
            L.Add(Rec!);
        }
        return (true, L);
    }

    public record StartCountRec(byte[] Hash, int Start, int Count) : IComparable<StartCountRec>
    {
        public int CompareTo(StartCountRec? other)
        {
            if (other == null) throw EX.New();
            return CmpHashBytes(Hash, other.Hash);
        }
    }

    /// <summary>
    /// 从已升序排序的List<HashFileNameRec>，构建升序排序的List<StartCountRec>
    /// </summary>
    /// <param name="l"></param>
    /// <returns></returns>
    public static List<StartCountRec> MakeStartCountList(List<HashFileNameRec> l)
    {
        var R = new List<StartCountRec>();

        //分组，假设List已排序
        int Cur = 0;
        while (Cur < l.Count)
        {
            //Cur指向当前hash值
            int Next = Cur + 1;
            //找相同hash值，直到结束或不同
            while ((Next < l.Count) && (Comparer<HashFileNameRec>.Default.Compare(l[Cur], l[Next]) == 0)) Next++;
            //Next指向结束位置(l.Count)或下一个不同元素位置
            R.Add(new StartCountRec(l[Cur].hash, Cur, Next - Cur));

            Cur = Next;
        }
        return R;
    }

    public record GroupRec(byte[] Hash, List<int> ImmuTreeIdxGroup);

    /// <summary>
    /// 从startCountList、hashFileNameList生成DictTree。
    /// 对每个不同hash值并行做
    ///     对相同hash值的不同FileName并行做
    ///         把FileName按"/"分割成路径列表，从根到叶子，
    ///         尝试打开读锁进入子节点（如失败则打开写锁进入子节点并新增子节点）。
    ///         在叶子节点保存跳到HashFileNameRec的方法。
    /// DictTreeNode包含：
    ///     PathFromRoot
    ///     Childs
    ///     StartCountListIdx为startCountList索引
    ///     StartOffset为StartCountRec中偏离Start的Offset
    /// 从DictTreeNode找到hashFileNameList索引方法：
    ///     startCountList[StartCountListIdx].Start + StartOffset
    /// </summary>
    /// <param name="startCountList"></param>
    /// <param name="hashFileNameList"></param>
    /// <returns></returns>
    public static DictTreeNode DictTreeFromStartCountList(List<StartCountRec> startCountList, List<HashFileNameRec> hashFileNameList)
    {
        //var Root = new FolderTree("", null);
        DictTreeNode Root = new DictTreeNode("");
        Parallel.ForEach(startCountList, (StartCntRec, _, StartCountListIdx) =>
        {
            //StartCntRec对应某个Hash值
            Parallel.For(0, StartCntRec.Count, (StartOffset) =>
            {
                //StartOffset对应某个Hash值中的某个文件名的偏移量
                var HashFileName = hashFileNameList[startCountList[(int)StartCountListIdx].Start + StartOffset];
                var paths = HashFileName.filename.Split('/'); //分解全路径成若干子路径，无论Win或Linux都按'/'
                //HashFileName形如"/child1/child2/child3"，最前面为"/"，
                //paths[1]为"child1"

                var CurTree = Root; //从根往叶子走
                for (int i = 1; i < paths.Length; i++)
                {
                    DictTreeNode NewChild; //将走到的新子树

                    //读当前树
                    CurTree.ChildsLock.EnterUpgradeableReadLock();
                    try
                    {
                        //当前子路径paths[i]是否在子树中
                        if (!CurTree.Childs.TryGetValue(paths[i], out NewChild))
                        {
                            //不存在，新增Child
                            NewChild = new DictTreeNode(CurTree.PathFromRoot + '/' + paths[i]);

                            CurTree.ChildsLock.EnterWriteLock();
                            try
                            {
                                CurTree.Childs[paths[i]] = NewChild;
                            }
                            finally
                            {
                                CurTree.ChildsLock.ExitWriteLock();
                            }
                        }
                    }
                    finally
                    {
                        CurTree.ChildsLock.ExitUpgradeableReadLock();
                    }
                    CurTree = NewChild;
                }
                //此时CurTree为叶子节点；
                //Childs.Count为0表示叶子节点，以下才有意义
                CurTree.StartCountListIdx = (int)StartCountListIdx;
                CurTree.StartOffset = StartOffset;
            });
        });
        return Root;
    }

    public class BytesComparer : IComparer<byte[]>
    {
        public int Compare(byte[]? x, byte[]? y)
        {
            if (x == null || y == null) throw EX.New();
            return CmpHashBytes(x, y);
        }
        public static BytesComparer Default { get; } = new BytesComparer();
    }

    /// <summary>
    /// 树节点，利用ImmutableDictionary可多线程安全从子节点路径读子节点，用于多任务查询树。
    /// </summary>
    public class ListTreeNode
    {
        public string PathFromRoot; //从根节点到当前节点的路径，形如"/a/b/c"
        public int Parent; //父节点在List<TreeNode>索引
        public ImmutableDictionary<string, int>? Childs; //子节点，string为子节点路径， int为子节点在List<TreeNode>索引

        //将删除
        public SortedSet<byte[]> AtLeast2SameChildsHashes; //all childs hashes

        //public List<int>? AtLeast2StartCountIdxList; //如节点为纯2节点，则为包含的所有不同Hash对应的StartCount索引列表；否则为空。
        public List<int>? HashParentIdxList; //如节点为纯2节点，则为包含的所有不同HashParent索引列表；否则为空。

        //初始化Parent = -1，Childs = null，且StartCountListIdx、StartOffset无意义
        public ListTreeNode(string pathFromRoot)
        {
            this.PathFromRoot = pathFromRoot;

            this.Parent = -1;
            this.Childs = null;
            //this.AtLeast2SameChildsHashes = new SortedSet<byte[]>(BytesComparer.Default);
            StartCountListIdx = -1;
            StartOffset = -1;
        }

        //Childs为null表示叶子节点，以下才有意义
        public int StartCountListIdx; //在List<StartCountRec>中的索引
        public int StartOffset; //StartCountRec中偏离Start的Offset
    }


    /// <summary>
    /// 从DictTree构建ListTree。
    /// 后序遍历，先加入子节点，然后加入父节点，再把子节点指向父节点、父节点包含所有子节点。
    /// ListTree.Childs是ImmutableDictionary，可多线程通过子路径名查找子节点。
    /// 返回的List[Count-1]为根节点，根节点的Parent为-1。
    /// 返回的List<ListTreeNode>为树，树节点的Parent、Childs为List索引，指向List中其他树节点。
    /// 返回的另一List<int>为跳转指针列表，可从HashFileNameList第i项跳到List<ListTreeNode>的第List<int>[i]项。
    /// </summary>
    /// <param name="r">DictTree根节点</param>
    /// <param name="hashFileNameList"></param>
    /// <param name="startCountList"></param>
    /// <returns></returns>
    public static async Task<(List<ListTreeNode>, List<int>)> DictTreeToListTree(DictTreeNode r, List<HashFileNameRec> hashFileNameList, List<StartCountRec> startCountList)
    {
        List<ListTreeNode> ListTree = new List<ListTreeNode>();

        //HashFileNameList第j个元素对应List<ListTreeNode>第HashFileNameListToTreeIdxList[j]个元素
        List<int> HashFileNameListToTreeIdxList = new List<int>(hashFileNameList.Count);
        for (int i = 0; i < hashFileNameList.Count; i++)
        {
            HashFileNameListToTreeIdxList.Add(-1);
        }

        //后序遍历根节点r，
        //每个节点对应的泛型值KeyValuePair<string, int>，string为在父节点中的子节点路径，int为节点在List<TreeNode>索引
        await PostTaskWalkDictTree<KeyValuePair<string, int>>(r, (node, childsValue) =>
        {
            //从节点node及子节点泛型值列表childsValue返回泛型值的函数如下

            //对于每个node节点，处理所有子节点对应的泛型KeyValuePair<string, int>列表，返回node节点对应的泛型KeyValuePair<string, int>。
            //泛型childsValue每项为KeyValuePair<string, int>，string为子节点路径，int为子节点在List<TreeNode>索引
            //由于后序遍历，此时node的所有子节点已加入List<TreeNode>树中
            var ListNode = new ListTreeNode(node.PathFromRoot);
            int ListIndex;
            lock (ListTree)
            {
                //向树添加ImmuNode并取得树中索引
                ListTree.Add(ListNode);
                ListIndex = ListTree.Count - 1;
            }

            if (childsValue.Count == 0)
            {
                //叶子节点
                ListNode.StartCountListIdx = node.StartCountListIdx;
                ListNode.StartOffset = node.StartOffset;
                var StartCount = startCountList[ListNode.StartCountListIdx];
                int HashFileNameListIdx = StartCount.Start + ListNode.StartOffset;
                lock (HashFileNameListToTreeIdxList)
                {
                    HashFileNameListToTreeIdxList[HashFileNameListIdx] = ListIndex;
                }
                if (StartCount.Count >= 2)
                {
                    //ImmuNode.AtLeast2StartCountIdxList = new List<int> { StartCountListIdx };
                    //ImmuNode.AtLeast2SameChildsHashes.Add(hashFileNameList[HashFileNameListIdx].hash);
                }
            }
            else if (childsValue.Count > 0)
            {
                //非叶子节点

                //修改Childs指向所有子节点
                var Builder = ImmutableDictionary.CreateBuilder<string, int>();
                foreach (var item in childsValue)
                {
                    Builder.Add(item.Key, item.Value);

                    //取得item对应节点ChildNode
                    ListTreeNode ChildNode;
                    lock (ListTree)
                    {
                        ChildNode = ListTree[item.Value];
                    }
                    //修改所有子节点的Parent指向ImmuIndex
                    ChildNode.Parent = ListIndex;

                    //加入ChildsHashes
                    //ImmuNode.AtLeast2SameChildsHashes.UnionWith(ChildImmuNode.AtLeast2SameChildsHashes);
                    //如果子节点均为纯2节点，则合并所有子节点的AtLeast2StartCountIdxList。
                }
                ListNode.Childs = Builder.ToImmutable();
            }
            else
            {
                throw EX.New();
            }

            //ImmuNode.PathFromRoot形如"/aa/bb/cc"，取<"cc", ImmuIndex>返回给父节点
            return new KeyValuePair<string, int>(Path.GetFileName(ListNode.PathFromRoot), ListIndex);
        });
        return (ListTree, HashFileNameListToTreeIdxList);
    }

    /// <summary>
    /// 后序Task遍历每个节点
    /// 从子节点提取包含的泛型ChildValue
    /// 多个子节点的泛型ChildValue合并成1个当前节点的泛型ChildsValue返回
    /// </summary>
    /// <typeparam name="T">每个节点包含的泛型</typeparam>
    /// <param name="n">要后序遍历的某个节点</param>
    /// <param name="a">从多个子节点ChildValue、当前节点，合并成当前节点对应的泛型Value返回的函数</param>
    /// <returns>1个ChildValue</returns>
    public static async Task<T> PostTaskWalkDictTree<T>(DictTreeNode n, Func<DictTreeNode, List<T>, T> a)
    {
        var Tasks = new List<Task>();
        List<T> L = new List<T>(); //每个节点对应1个泛型值，n节点的所有子节点对应的泛型值列表
        foreach (var item in n.Childs)
        {
            //对每个item子节点启动1个Task做
            Tasks.Add(Task.Run(async () =>
            {
                T OneChildValue = await PostTaskWalkDictTree<T>(item.Value, a); //遍历item子节点及其所有子节点
                lock (L)
                {
                    L.Add(OneChildValue);
                }
            }));
        }
        await Task.WhenAll(Tasks);
        return a(n, L); //调用a函数，参数为n节点及其子节点泛型值列表，得到n节点对应泛型值。
    }

    /// <summary>
    /// 前序Task遍历每个节点
    /// 把父节点传入的ParentValue修改后传给所有Child
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="n"></param>
    /// <param name="a"></param>
    /// <param name="parentValue"></param>
    /// <returns></returns>
    public static async Task PreTaskWalkDictTree<T>(DictTreeNode n, Func<DictTreeNode, T, T> a, T parentValue)
    {
        var ChildValue = a(n, parentValue);
        var Tasks = new List<Task>();
        foreach (var item in n.Childs)
        {
            Tasks.Add(Task.Run(async () =>
            {
                await PreTaskWalkDictTree(item.Value, a, ChildValue);
            }));
        }
        await Task.WhenAll(Tasks);
    }


    public static async Task PreTaskWalkImmuTreeSuccessNotChild(List<ListTreeNode> immuTreeList, int nodeIdx, Func<int, bool> f)
    {
        if (!f(nodeIdx))
        {
            var Childs = immuTreeList[nodeIdx].Childs;
            foreach (var item in Childs.EmptyIfNull())
            {
                await PreTaskWalkImmuTreeSuccessNotChild(immuTreeList, item.Value, f);
            }
        }
    }

    /// <summary>
    /// </summary>
    class HashParents
    {
        public int StartCountIdx; //List<StartCountRec>的索引，StartCountRec.Count >= 2
        public List<int> ParentIdxList; //List<ListTreeNode>树中，该Hash对应文件列表对应的叶子节点的所有父节点列表(除根)的索引，按升序排序
    }

    /// <summary>
    /// 构造HashParents列表(除根)。
    /// 对每个Hash，如对应文件名数量大于等于2，则构造HashParents。
    /// 
    /// 取得Hash对应文件列表对应的List<ListTreeNode>树叶子节点列表，作为当前列表集合，初始结果集为空。
    /// 循环，如当前列表集合非空：
    ///     当前列表集合并入结果集；
    ///     当前列表集合 = 当前列表集合的上一级父节点列表集合(除根)；
    /// 结果集排升序返回。
    /// </summary>
    /// <param name="startCountList"></param>
    /// <param name="listTree"></param>
    /// <param name="hashFileNameListToListTreeIdxList"></param>
    /// <returns></returns>
    static List<HashParents> MakeHashParents(
        List<StartCountRec> startCountList,
        List<ListTreeNode> listTree,
        List<int> hashFileNameListToListTreeIdxList)
    {
        var R = new List<HashParents>();
        var AtLeastTwoIdx = Enumerable.Range(0, startCountList.Count).Where(x => startCountList[x].Count >= 2);
        //var AtLeastTwo = startCountList.Where((i) => (i.Count >= 2));
        //得到Hash对应文件名数量大于等于2的组

        //对AtLeastTwo的所有组做
        foreach (var Idx in AtLeastTwoIdx)
        {
            var StartCountItem = startCountList[Idx];
            //对一组的所有文件名做
            int Cur = StartCountItem.Start;
            var CurSet = new List<int>(); //当前列表集合
            for (int i = 0; i < StartCountItem.Count; i++)
            {
                //文件名索引 Cur+i
                //List<ListTreeNode>索引
                var LeafIdx = hashFileNameListToListTreeIdxList[Cur + i];
                CurSet.Add(LeafIdx);
            }
            CurSet.Sort(); //默认升序排序
            var ResultSet = new List<int>(); //结果列表集合
            while (CurSet.Count > 0)
            {
                //当前列表集合并入结果集；
                ResultSet=ResultSet.SetUnion(CurSet); //升序排序

                //当前列表集合 = 当前列表集合的上一级父节点列表集合(除根)；
                var ParentSet = new List<int>();
                foreach (int i in CurSet)
                {
                    var Parent = listTree[i].Parent;
                    if (GetListTreeRootIndex(listTree) == Parent) continue; //如果是根，忽略

                    ParentSet.SetUnionOne(Parent); //把父节点并入父节点集合
                }
                CurSet = ParentSet;
            }

            //var TTT = new HashParents() { Hash = StartCountItem.Hash};
            //var TTT = new HashParents() { StartCountItem.Hash, ResultSet };
            R.Add(new HashParents() { StartCountIdx = Idx, ParentIdxList = ResultSet });

        }
        return R;
    }



    /// <summary>
    /// 构造潜在非父包含节点集--最上层Atleast2节点集(除根)，每个节点仅由Hash数量最少为2的叶子节点自下而上构成，
    /// 且其中无一节点为另一节点的父节点。
    /// 
    /// 从Atleast2叶子节点集开始，自底向上，查找最上层Atleast2节点集(MostTopAtleast2NodeSet)。
    /// MostTopAtleast2NodeSet 置为 空。
    /// 所有Atleast2节点集(AllAtleast2NodeSet)、当前层节点集(CurLevelNodeSet) 置为 Atleast2叶子节点集。
    /// while(CurLevelNodeSet非空)
    /// {
    ///     ParentLevelNodeSet = 空
    ///     foreach(CurNode in CurLevelNodeSet)
    ///     {
    ///         如 CurNode为根 则 continue
    ///         ParentNode = CurNode.Parent
    ///         如 ParentNode in AllAtleast2NodeSet 则 continue
    ///         如 ParentNode的每个子节点 in AllAtleast2NodeSet 那么
    ///             ParentLevelNodeSet += ParentNode
    ///             AllAtleast2NodeSet += ParentNode
    ///         else
    ///             MostTopAtleast2NodeSet += CurNode
    ///     }
    ///     CurLevelNodeSet = ParentLevelNodeSet
    /// }
    /// 
    /// </summary>
    /// <param name="startCountList"></param>
    /// <param name="immuTreeList"></param>
    /// <param name="hashFileNameListToImmuListIndexList"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    static List<int> MakeMostTopAtleast2Old(
        List<StartCountRec> startCountList,
        List<ListTreeNode> immuTreeList,
        List<int> hashFileNameListToImmuListIndexList)
    {
        var MostTopAtleast2NodeSet = new List<int>();
        var AtLeastTwoStartCount = startCountList.Where((i) => (i.Count >= 2));
        var CurLevelNodeSet = new List<int>();
        foreach (var StartCountItem in AtLeastTwoStartCount)
        {
            //对一组的所有文件名做
            int Cur = StartCountItem.Start;
            for (int i = 0; i < StartCountItem.Count; i++)
            {
                //文件名索引 Cur+i
                //ImmuTree索引
                var LeafIdx = hashFileNameListToImmuListIndexList[Cur + i];
                CurLevelNodeSet.Add(LeafIdx);
            }
        }
        CurLevelNodeSet.Sort(); //默认升序排序
        var AllAtleast2NodeSet = new List<int>(CurLevelNodeSet);

        var RootIndex = GetListTreeRootIndex(immuTreeList);
        while (CurLevelNodeSet.Count > 0)
        {
            var ParentLevelNodeSet = new List<int>();
            foreach (var CurNode in CurLevelNodeSet)
            {
                if (CurNode == RootIndex) continue;
                var ParentNode = immuTreeList[CurNode].Parent;
                if (AllAtleast2NodeSet.SetContain(ParentNode)) continue;

                bool AllChildInAllAtleast2NodeSet = true;
                foreach (var ChildNodeItem in immuTreeList[ParentNode].Childs.EmptyIfNull())
                {
                    if (!AllAtleast2NodeSet.SetContain(ChildNodeItem.Value))
                    {
                        AllChildInAllAtleast2NodeSet = false;
                        break;
                    }
                }
                if (AllChildInAllAtleast2NodeSet)
                {
                    ParentLevelNodeSet.SetUnionOne(ParentNode);
                    AllAtleast2NodeSet.SetUnionOne(ParentNode);
                }
                else
                {
                    MostTopAtleast2NodeSet.SetUnionOne(CurNode);
                }
            }
            CurLevelNodeSet = ParentLevelNodeSet;
        }
        return MostTopAtleast2NodeSet;
    }

    /// <summary>
    /// 构造潜在非父包含节点集--最上层纯2节点集(除根)，每个纯2节点仅由纯2叶子节点自下而上构成，
    /// 且其中无一节点为另一节点的父节点。
    /// 
    /// 纯2节点定义：如某节点为叶子节点，则其相同Hash数量最少为2；如某节点非叶子节点，则其子节点均为纯2节点。
    /// 非2节点定义：不是纯2节点。如某节点为叶子节点，则其相同Hash数量为1；如某节点非叶子节点，则其子节点至少含有1个非2节点。
    /// 
    /// 纯2节点的HashParentIdxList设置为其包含的所有Hash在HashParents中索引的集合；
    /// 
    /// 递归调用PostTaskWalkListTree()后序遍历树(含根)：
    ///     纯2叶子节点返回其自身单元素集合，非2叶子节点返回空；
    ///     纯2非叶子节点返回其自身单元素集合；
    ///     非2非叶子节点，返回其子节点对应返回集合的并集；
    ///     
    /// 假设标记后缀为2的节点为纯2节点，
    ///.
    ///├── a2
    ///│   ├── c2
    ///│   └── d2
    ///└── b
    ///    ├── e2
    ///    │   ├── i2
    ///    │   └── j2
    ///    ├── f2
    ///    │   ├── k2
    ///    │   └── l2
    ///    ├── g
    ///    │   └── m
    ///    └── h2
    /// 则上图PostTaskWalkListTree(RootNode)结果为{a2,e2,f2,h2}
    /// </summary>
    /// <param name="hashParents"></param>
    /// <param name="startCountList"></param>
    /// <param name="listTree"></param>
    /// <param name="hashFileNameListToListTreeIdxList"></param>
    /// <returns>节点索引集</returns>
    static async Task<List<int>> MakeMostTopPure2(
        List<HashParents> hashParents,
        List<StartCountRec> startCountList,
        List<ListTreeNode> listTree,
        List<int> hashFileNameListToListTreeIdxList)
    {
        //var MostTopAtleast2NodeSet = new List<int>();
        var RootNode = GetListTreeRootIndex(listTree);

        var MostTopAtleast2NodeSet = await PostTaskWalkListTree<List<int>>(listTree, RootNode, (node, childValues) =>
        {
            var Result = new List<int>();
            var ListTreeNode = listTree[node];
            if (null == ListTreeNode.Childs)
            {
                //叶子节点
                var StartCountListIdx = ListTreeNode.StartCountListIdx;
                if (startCountList[StartCountListIdx].Count >= 2)
                {
                    //纯2叶子节点
                    //在hashParents中查找对应的StartCountIdx
                    int Index = (from i in Enumerable.Range(0, hashParents.Count)
                                 where hashParents[i].StartCountIdx == StartCountListIdx
                                 select i).Single();
                    ListTreeNode.HashParentIdxList = new List<int>() { Index };

                    Result.SetUnionOne(node); //纯2叶子节点返回其自身单元素集合
                }
                else
                {
                    //非2叶子节点
                    //返回空
                }
            }
            else
            {
                //非叶子节点
                bool AllChildPure2 = true; //假设所有子节点纯2
                foreach (var item in ListTreeNode.Childs)
                {
                    if (listTree[item.Value].HashParentIdxList == null)
                    {
                        //非2节点
                        AllChildPure2 = false;
                        break;
                    }
                }
                if (AllChildPure2)
                {
                    //纯2非叶子节点
                    //HashParentIdxList为合并子节点的值
                    ListTreeNode.HashParentIdxList = new List<int>();
                    foreach (var item in ListTreeNode.Childs)
                    {
                        ListTreeNode.HashParentIdxList=ListTreeNode.HashParentIdxList.SetUnion(
                            listTree[item.Value].HashParentIdxList ?? throw EX.New());
                    }
                    //纯2非叶子节点返回其自身单元素集合
                    Result.SetUnionOne(node);
                }
                else
                {
                    //非2非叶子节点，返回其子节点对应返回集合的并集；
                    foreach (var item in childValues)
                    {
                        Result=Result.SetUnion(item);
                    }
                }
            }
            return Result;
        });
        return MostTopAtleast2NodeSet;
    }




    public static IEnumerable<T> EmptyIfNull<T>(this IEnumerable<T>? nullableenumerable) =>
        nullableenumerable ?? Enumerable.Empty<T>();


    /// <summary>
    /// 后序Task遍历每个节点
    /// 从子节点提取包含的泛型Value
    /// 多个子节点的泛型Value合并成1个当前节点的泛型Value返回
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="n"></param>
    /// <param name="a">从多个子节点泛型Value、当前节点，合并成当前节点对应的泛型Value返回的函数</param>
    /// <returns>1个泛型Value</returns>
    public static async Task<T> PostTaskWalkListTree<T>(List<ListTreeNode> tree, int n, Func<int, List<T>, T> a)
    {
        var Tasks = new List<Task>();
        List<T> L = new List<T>();

        foreach (var item in tree[n].Childs.EmptyIfNull())
        {
            Tasks.Add(Task.Run(async () =>
            {
                T OneChildValue = await PostTaskWalkListTree<T>(tree, item.Value, a);
                lock (L)
                {
                    L.Add(OneChildValue);
                }
            }));
        }
        await Task.WhenAll(Tasks);
        return a(n, L);
    }


    public static async Task<T> PostTaskWalkListTreeDebug<T>(List<ListTreeNode> tree, int n, Func<int, List<T>, T> a)
    {
        //var Tasks = new List<Task>();
        List<T> L = new List<T>();

        foreach (var item in tree[n].Childs.EmptyIfNull())
        {
            //Tasks.Add(Task.Run(async () =>
            //{
                T OneChildValue = await PostTaskWalkListTreeDebug<T>(tree, item.Value, a);
                lock (L)
                {
                    L.Add(OneChildValue);
                }
            //}));
        }
        //await Task.WhenAll(Tasks);
        return a(n, L);
    }


    class SameHashAtleast2ImmuGroup : IComparable<SameHashAtleast2ImmuGroup>
    {
        public byte[] Hash;
        public List<int> ImmuTreeIdxList; //ImmuTree中包含该文件Hash的所有节点(除根)
        public List<int> Childs; //与ImmuTreeIdxList等长一一对应，每项对应其所有子节点在ImmuTreeIdxList中索引
        public List<int> RootChilds; //ImmuTree中包含该文件Hash的所有节点的根，包含的第一级子节点在ImmuTreeIdxList中索引
        public SameHashAtleast2ImmuGroup(byte[] hash, List<int> immuTreeIdxList)
        {
            this.Hash = hash;
            this.ImmuTreeIdxList = immuTreeIdxList;
        }
        public int CompareTo(SameHashAtleast2ImmuGroup? other)
        {
            if (other == null) throw EX.New();
            return CmpHashBytes(Hash, other.Hash);
        }
    }

    /// <summary>
    /// 按Hash分组，每组数量大于等于2的，其在ImmuTree内所有叶节点及其父目录(除根)的全部索引。
    /// 按Hash值升序排序。
    /// </summary>
    /// <param name="immuTreeList"></param>
    /// <param name="startCountList"></param>
    /// <param name="HashFileNameToImmuTreeIdxList"></param>
    /// <returns></returns>
    static List<SameHashAtleast2ImmuGroup> MakeImmuGroup(List<ListTreeNode> immuTreeList, List<StartCountRec> startCountList, List<int> HashFileNameToImmuTreeIdxList)
    {
        var R = new List<SameHashAtleast2ImmuGroup>();
        var AtLeastTwo = startCountList.Where((i) => (i.Count > 1)).ToList();
        //得到组成员数量至少大于等于2的组

        //对所有组做
        foreach (var StartCountItem in AtLeastTwo)
        {
            //对一组做
            var GroupUnionList = new List<int>();
            int Cur = StartCountItem.Start;
            for (int i = 0; i < StartCountItem.Count; i++)
            {
                //Cur+i
                //对hash相同的组的一个文件项做
                var LeafIdx = HashFileNameToImmuTreeIdxList[Cur + i];
                var HashFileImmuIdxList = NodeToExclusiveRoot(LeafIdx, immuTreeList);
                //要求LeafToExclusiveRoot返回升序排序
                GroupUnionList = GroupUnionList.SetUnion(HashFileImmuIdxList);
            }
            //利用List.BinarySearch()查找插入位置
            //生成SameHashAtleast2ImmuGroup.Childs、RootChilds
            //
            R.Add(new(StartCountItem.Hash, GroupUnionList));
        }
        return R;
    }

    /// <summary>
    /// 返回从节点到根节点的所有索引，包含节点及根节点，已升序排序
    /// </summary>
    /// <param name="nodeIdx"></param>
    /// <param name="listTree"></param>
    /// <returns></returns>
    private static List<int> NodeToRoot(int nodeIdx, List<ListTreeNode> listTree)
    {
        var R = new List<int>();
        int RootIdx = GetListTreeRootIndex(listTree);
        int CurIdx = nodeIdx;
        while (CurIdx != RootIdx)
        {
            R.Add(CurIdx);
            CurIdx = listTree[CurIdx].Parent;
        }
        R.Add(RootIdx);
        R.Sort();
        return R;
    }

    /// <summary>
    /// 返回从节点到根节点的所有索引，不包含根节点，已升序排序
    /// </summary>
    /// <param name="nodeIdx"></param>
    /// <param name="immuTreeList"></param>
    /// <returns></returns>
    private static List<int> NodeToExclusiveRoot(int nodeIdx, List<ListTreeNode> immuTreeList)
    {
        var R = new List<int>();
        int RootIdx = GetListTreeRootIndex(immuTreeList);
        int CurIdx = nodeIdx;
        while (CurIdx != RootIdx)
        {
            R.Add(CurIdx);
            CurIdx = immuTreeList[CurIdx].Parent;
        }
        R.Sort();
        return R;
    }
    /// <summary>
    /// 返回从节点到根节点的所有索引，不包含该节点，不包含根节点，已升序排序
    /// </summary>
    /// <param name="nodeIdx">要求不等于Root节点</param>
    /// <param name="immuTreeList"></param>
    /// <returns></returns>
    private static List<int> ExclusiveNodeToExclusiveRoot(int nodeIdx, List<ListTreeNode> immuTreeList)
    {
        var R = new List<int>();
        int RootIdx = GetListTreeRootIndex(immuTreeList);
        int CurIdx = nodeIdx;
        CurIdx = immuTreeList[CurIdx].Parent; //多这一行
        while (CurIdx != RootIdx)
        {
            R.Add(CurIdx);
            CurIdx = immuTreeList[CurIdx].Parent;
        }
        R.Sort();
        return R;
    }

    /// <summary>
    /// 去除immuIdxList中的全部父目录项
    /// </summary>
    /// <param name="immuIdxList"></param>
    /// <param name="immuTreeList"></param>
    private static List<int> RemoveImmuParent(List<int> immuIdxList, List<ListTreeNode> immuTreeList)
    {
        var R = immuIdxList;
        for (int i = 0; i < immuIdxList.Count; i++)
        {
            R = R.SetSub(ExclusiveNodeToExclusiveRoot(immuIdxList[i], immuTreeList));
        }
        return R;
    }
    private static int GetListTreeRootIndex(List<ListTreeNode> listTree) => listTree.Count - 1;

    private static bool IsParent(List<ListTreeNode> immuTreeList, int root, int child, int parent)
    {
        bool bIsParent = false;
        int Cur = child;
        while (Cur != root)
        {
            if (Cur == parent)
            {
                bIsParent = true;
                break;
            }
            Cur = immuTreeList[Cur].Parent;
        }
        return bIsParent;
    }

    /// <summary>
    /// 求两个List<T>的交集，要求one、two已升序排序，返回结果已排序
    /// </summary>
    /// <param name="one"></param>
    /// <param name="two"></param>
    /// <returns></returns>
    private static List<T> SetIntersect<T>(this List<T> one, List<T> two, Comparer<T>? comparer = null)
    {
        var comparison = (comparer ?? Comparer<T>.Default).Compare;

        var R = new List<T>();
        int OneIndex = 0;
        int TwoIndex = 0;
        while ((OneIndex < one.Count) && (TwoIndex < two.Count))
        {
            var OneValue = one[OneIndex];
            var TwoValue = two[TwoIndex];
            //默认升序排序
            if (comparison(OneValue, TwoValue) > 0)
            {
                //two前进
                TwoIndex++;
            }
            else if (comparison(TwoValue, OneValue) > 0)
            {
                //one前进
                OneIndex++;
            }
            else
            {
                //找到相等, one、two前进
                R.Add(OneValue);
                OneIndex++;
                TwoIndex++;
            }
        }

        return R;
    }

    /// <summary>
    /// 从已升序排序的被减数minuend中减去已升序排序的减数subtrahend，返回已升序排序的差。
    /// 算法：
    ///     两个队列从头遍历直到任一方到尾，比较元素大小，被减数队头小于减数对头则加入返回队列。
    ///     如被减数仍有剩余，则全部加入返回队列。
    /// </summary>
    /// <param name="minuend"></param>
    /// <param name="subtrahend"></param>
    /// <returns></returns>
    public static List<T> SetSub<T>(this List<T> minuend, List<T> subtrahend)
    {
        int MinIndex = 0;
        int SubIndex = 0;
        var R = new List<T>();
        while ((MinIndex < minuend.Count) && (SubIndex < subtrahend.Count))
        {
            var MinValue = minuend[MinIndex];
            var SubValue = subtrahend[SubIndex];
            if (Comparer<T>.Default.Compare(MinValue, SubValue) > 0)
            {
                //Minuend > Subtrahend
                SubIndex++;
            }
            else if (Comparer<T>.Default.Compare(SubValue, MinValue) > 0)
            {
                //Subtrahend > Minuend
                R.Add(MinValue);
                MinIndex++;
            }
            else
            {
                //Subtrahend == Minuend
                MinIndex++;
                SubIndex++;
            }
        }
        while (MinIndex < minuend.Count)
        {
            R.Add(minuend[MinIndex]);
            MinIndex++;
        }
        return R;
    }

    /// <summary>
    /// 从已升序排序的被减数minuend中减去已升序排序的减数subtrahend，返回已升序排序的差。
    /// 算法：
    ///     
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="minuend"></param>
    /// <param name="subtrahend"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public static List<T> SetQuickSub<T>(this List<T> minuend, List<T> subtrahend, Comparer<T>? comparer = null)
    {
        int MinIndex = 0;
        int SubIndex = 0;
        var R = new List<T>();
        while ((MinIndex < minuend.Count) && (SubIndex < subtrahend.Count))
        {
            //var MinValue = minuend[MinIndex];
            var SubValue = subtrahend[SubIndex];

            var i = minuend.BinarySearch(MinIndex, minuend.Count - MinIndex, SubValue, comparer ?? Comparer<T>.Default);
            if (i >= 0)
            {
                //找到的位置，把列表分为左右两部分
                //R.AddRange(minuend.GetRange(MinIndex, i - MinIndex));
                R.AddRange(minuend.EnumRange(MinIndex, i - MinIndex)); //i - MinIndex == 0时，为空
                MinIndex = i + 1;
                SubIndex++;
            }
            else
            {
                //没找到，~i为下一个大于SubValue的元素的位置，如没有更大元素则~i为要查找队列的后一位。
                R.AddRange(minuend.EnumRange(MinIndex, ~i - MinIndex));
                MinIndex = ~i;
                //当前SubValue处理完，移到下一个SubIndex
                SubIndex++;
            }

        }
        //minuend已找完，或者，subtrahend已找完
        if (MinIndex < minuend.Count)
        {
            //minuend未找完，且subtrahend已找完
            R.AddRange(minuend.EnumRange(MinIndex, minuend.Count - MinIndex));
        }

        return R;
    }

    public static IEnumerable<T> EnumRange<T>(this List<T> l, int start, int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return l[start];
            start++;
        }
    }

    /// <summary>
    /// 从升序排序的两个List(每个List不含重复元素)，返回升序排序的并集。
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="one"></param>
    /// <param name="two"></param>
    /// <returns></returns>
    public static List<T> SetUnion<T>(this List<T> one, List<T> two)
    {
        var R = new List<T>();
        int OneIndex = 0;
        int TwoIndex = 0;
        while ((OneIndex < one.Count) && (TwoIndex < two.Count))
        {
            var OneValue = one[OneIndex];
            var TwoValue = two[TwoIndex];
            //默认升序排序
            if (Comparer<T>.Default.Compare(OneValue, TwoValue) < 0)
            {
                //One小
                R.Add(OneValue);
                OneIndex++;
            }
            else if (Comparer<T>.Default.Compare(TwoValue, OneValue) < 0)
            {
                //Two小
                R.Add(TwoValue);
                TwoIndex++;
            }
            else
            {
                //相等, one、two同时前进
                R.Add(OneValue);
                OneIndex++;
                TwoIndex++;
            }
        }

        //One、Two可能还有剩余
        while (OneIndex < one.Count)
        {
            R.Add(one[OneIndex]);
            OneIndex++;
        }
        while (TwoIndex < two.Count)
        {
            R.Add(two[TwoIndex]);
            TwoIndex++;
        }

        return R;
    }


    /// <summary>
    /// 向升序排序的泛型List集合，添加泛型元素。
    /// 如集合中已存在该元素，则忽略。
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="l"></param>
    /// <param name="one"></param>
    /// <returns></returns>
    public static void SetUnionOne<T>(this List<T> l, T one)
    {
        var i = l.BinarySearch(one);
        if (i < 0)
        {
            //没找到
            l.Insert(~i, one);
        }
    }

    /// <summary>
    /// 升序排序的泛型List集合是否包含元素。
    /// </summary>
    /// <typeparam name="T">泛型</typeparam>
    /// <param name="l">集合</param>
    /// <param name="e">元素</param>
    /// <returns></returns>
    public static bool SetContain<T>(this List<T> l, T e)
    {
        var i = l.BinarySearch(e);
        if (i < 0)
        {
            //没找到
            return false;
        }
        else
            return true;
    }

    public class ListTask<T>
    {
        private List<T> _list;
        private Action<T> _action;
        private int _runningCount;
        /// <summary>
        /// 添加列表首元素，并分配任务
        /// </summary>
        /// <param name="one"></param>
        /// <param name="action"></param>
        public void Start(T one, Action<T> action)
        {
            lock (this)
            {
                _list = new List<T>() { one };
                _action = action;
                AllocateTask();
            }
        }
        /// <summary>
        /// 用最大可用线程分配任务
        /// </summary>
        private void AllocateTask()
        {
            int workerThreads;
            ThreadPool.GetMaxThreads(out workerThreads, out _);
        }

    }

}
