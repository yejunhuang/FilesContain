using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace CmdsNameSpace;

public static partial class Cmds
{
    /// <summary>
    /// 并发树节点，用于多线程生成树
    /// </summary>
    public class DictTreeNode
    {
        public string PathFromRoot; //从根节点到当前节点的路径，如./a/b/c
        //public ConcurrentDictionary<string, ConcurrentTreeNode> Childs; //子节点，string为子节点路径，
        public Dictionary<string, DictTreeNode> Childs; //子节点，string为子节点路径，
        public ReaderWriterLockSlim ChildsLock;

        //Childs.Count为0表示叶子节点
        public int ToHashFileNameRecListIndex; //在List<HashFileNameRec>中索引
    }

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
    static (ErrorString, HashFileNameRec?) HashFileRecFromStr(string s, Func<string, string>? fileNameConvert = null)
    {
        int i = s.IndexOf(' ');
        if (i <= 0) return ("HashFileRecFromStr() s.IndexOf(' ') <= 0", default);
        var HashStr = s.Substring(0, i);
        var Hash = Convert.FromHexString(HashStr);
        var FileName = s.Substring(i + 1);
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
        //var Groups = new SortedSet<HashStartCountRec>(HashStartCountRecComparer.Default);
        var R = new List<StartCountRec>();

        //分组，假设List已排序
        int Cur = 0;
        while (Cur < l.Count)
        {
            //Start指向当前hash值
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

    public static DictTreeNode DictTreeFromHashFileNameList(List<HashFileNameRec> l)
    {
        //var Root = new FolderTree("", null);
        var Root = new DictTreeNode()
        {
            PathFromRoot = "",
            Childs = new Dictionary<string, DictTreeNode>(),
            ChildsLock = new ReaderWriterLockSlim()
        };
        Parallel.ForEach(l, (hashFileNameRec, _, ListIndex) =>
        {
            var paths = hashFileNameRec.filename.Split(Path.AltDirectorySeparatorChar); //分解全路径成若干子路径
            var CurTree = Root; //从根往叶子走
            for (int i = 1; i < paths.Length; i++)
            {
                DictTreeNode? NewChild; //将走到的新子树

                //读当前树
                CurTree.ChildsLock.EnterUpgradeableReadLock();
                try
                {
                    //当前子路径paths[i]是否在子树中
                    if (!CurTree.Childs.TryGetValue(paths[i], out NewChild))
                    {
                        //不存在，新增Child
                        NewChild = new DictTreeNode()
                        {
                            PathFromRoot = CurTree.PathFromRoot + Path.AltDirectorySeparatorChar + paths[i],
                            Childs = new Dictionary<string, DictTreeNode>(),
                            ChildsLock = new ReaderWriterLockSlim()
                        };

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
            CurTree.ToHashFileNameRecListIndex = (int)ListIndex;
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
    /// 线程安全树节点，用于多线程查询树
    /// </summary>
    public class ImmutableTreeNode
    {
        public string PathFromRoot; //从根节点到当前节点的路径，如./a/b/c
        public int Parent; //父节点
        public ImmutableDictionary<string, int>? Childs; //子节点，string为子节点路径， int为子节点在List<TreeNode>索引

        //Childs为null表示叶子节点
        public int ToHashFileNameRecListIndex; //在List<HashFileNameRec>中索引

        public SortedSet<byte[]> ChildsHashes; //all childs hashes
    }
    //树用List<ImmutableTreeNode>表示，Parent、Childs用List的索引值表示。
    //另用List<int>存储，List<HashFileNameRec>对应位置指向List<ImmutableTreeNode>树叶子节点的索引。

    /// <summary>
    /// 从DictTree构建ImmuTree。
    /// 返回的List<ImmutableTreeNode>为树，树节点的Parent、Childs为List索引，指向List中其他树节点。
    /// 返回的List[Count-1]为根节点。
    /// 返回的另一List<int>为指针列表，可用来查找HashFileNameList第n项指向ImmuList哪项。
    /// </summary>
    /// <param name="r"></param>
    /// <param name="hashFileNameRecListCount"></param>
    /// <returns></returns>
    public static async Task<(List<ImmutableTreeNode>, List<int>)> DictTreeToImmutableTree(DictTreeNode r, List<HashFileNameRec> HashFileNameList)
    {
        List<ImmutableTreeNode> ImmuTreeList = new List<ImmutableTreeNode>();

        //HashFileNameList第j个元素对应ImmuList第HashFileNameListToImmuListIndexList[j]个元素
        List<int> HashFileNameListToImmuListIndexList = new List<int>(HashFileNameList.Count);
        for (int i = 0; i < HashFileNameList.Count; i++)
        {
            HashFileNameListToImmuListIndexList.Add(-2);
        }


        object TreeListAndIndexListLock = new object(); //所有读写2个List的操作均lock此对象

        await PostTaskWalkDictTree<KeyValuePair<string, int>>(r, (node, childsValue) =>
        {
            var ImmuNode = new ImmutableTreeNode()
            {
                PathFromRoot = node.PathFromRoot,
                Parent = -1,
                Childs = null,
                ToHashFileNameRecListIndex = 0,
                ChildsHashes = new SortedSet<byte[]>(BytesComparer.Default)
            };
            int ImmuIndex;
            lock (TreeListAndIndexListLock)
            {
                ImmuTreeList.Add(ImmuNode);
                ImmuIndex = ImmuTreeList.Count - 1;
            }

            if (childsValue.Count > 0)
            {
                //非叶子节点

                //修改Childs指向所有子节点
                var Builder = ImmutableDictionary.CreateBuilder<string, int>();
                foreach (var item in childsValue)
                {
                    Builder.Add(item.Key, item.Value);

                    //取得Immu子节点
                    ImmutableTreeNode ChildImmuNode;
                    lock (TreeListAndIndexListLock)
                    {
                        ChildImmuNode = ImmuTreeList[item.Value];
                    }
                    //修改所有子节点的Parent指向ImmuIndex
                    ChildImmuNode.Parent = ImmuIndex;

                    //加入ChildsHashes
                    ImmuNode.ChildsHashes.UnionWith(ChildImmuNode.ChildsHashes);
                }
                ImmuNode.Childs = Builder.ToImmutable();
            }
            else
            {
                //叶子节点
                ImmuNode.ToHashFileNameRecListIndex = node.ToHashFileNameRecListIndex;
                lock (TreeListAndIndexListLock)
                {
                    HashFileNameListToImmuListIndexList[node.ToHashFileNameRecListIndex] = ImmuIndex;
                }
                ImmuNode.ChildsHashes.Add(HashFileNameList[node.ToHashFileNameRecListIndex].hash);
            }

            return new KeyValuePair<string, int>(Path.GetFileName(ImmuNode.PathFromRoot), ImmuIndex);
        });
        return (ImmuTreeList, HashFileNameListToImmuListIndexList);
    }

    /// <summary>
    /// 后序Task遍历每个节点
    /// 从子节点提取ChildValue
    /// 多个子节点的ChildValue合并成1个ChildsValue返回
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="n"></param>
    /// <param name="a">多个子节点ChildValue合并成1个ChildsValue返回的函数</param>
    /// <returns>1个ChildValue</returns>
    public static async Task<T> PostTaskWalkDictTree<T>(DictTreeNode n, Func<DictTreeNode, List<T>, T> a)
    {
        var Tasks = new List<Task>();
        List<T> L = new List<T>();
        foreach (var item in n.Childs)
        {
            Tasks.Add(Task.Run(async () =>
            {
                T OneChildValue = await PostTaskWalkDictTree<T>(item.Value, a);
                lock (L)
                {
                    L.Add(OneChildValue);
                }
            }));
        }
        await Task.WhenAll(Tasks);
        return a(n, L);
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


    class SameHashAtleast2ImmuGroup : IComparable<SameHashAtleast2ImmuGroup>
    {
        public byte[] Hash;
        public List<int> ImmuTreeIdxList;
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
    static List<SameHashAtleast2ImmuGroup> MakeImmuGroup(List<ImmutableTreeNode> immuTreeList, List<StartCountRec> startCountList, List<int> HashFileNameToImmuTreeIdxList)
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
                GroupUnionList = GroupUnionList.Union(HashFileImmuIdxList);
            }
            R.Add(new(StartCountItem.Hash, GroupUnionList));
        }
        return R;
    }

    /// <summary>
    /// 返回从节点到根节点的所有索引，不包含根节点，已升序排序
    /// </summary>
    /// <param name="leafIdx"></param>
    /// <param name="immuTreeList"></param>
    /// <returns></returns>
    private static List<int> NodeToExclusiveRoot(int leafIdx, List<ImmutableTreeNode> immuTreeList)
    {
        var R = new List<int>();
        int RootIdx = GetImmuTreeRootIndex(immuTreeList);
        int CurIdx = leafIdx;
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
    /// <param name="leafIdx">要求不等于Root节点</param>
    /// <param name="immuTreeList"></param>
    /// <returns></returns>
    private static List<int> ExclusiveNodeToExclusiveRoot(int leafIdx, List<ImmutableTreeNode> immuTreeList)
    {
        var R = new List<int>();
        int RootIdx = GetImmuTreeRootIndex(immuTreeList);
        int CurIdx = leafIdx;
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
    private static List<int> RemoveImmuParent(List<int> immuIdxList, List<ImmutableTreeNode> immuTreeList)
    {
        var R = immuIdxList;
        for (int i = 0; i < immuIdxList.Count; i++)
        {
            R = R.Sub(ExclusiveNodeToExclusiveRoot(immuIdxList[i], immuTreeList));
        }
        return R;
    }
    private static int GetImmuTreeRootIndex(List<ImmutableTreeNode> immuTreeList) => immuTreeList.Count - 1;

    private static bool IsParent(List<ImmutableTreeNode> immuTreeList, int root, int child, int parent)
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
    private static List<T> SortedIntersect<T>(this List<T> one, List<T> two)
    {
        var R = new List<T>();
        int OneIndex = 0;
        int TwoIndex = 0;
        while ((OneIndex < one.Count) && (TwoIndex < two.Count))
        {
            var OneValue = one[OneIndex];
            var TwoValue = two[TwoIndex];
            //默认升序排序
            if (Comparer<T>.Default.Compare(OneValue, TwoValue) > 0)
            {
                //two前进
                TwoIndex++;
            }
            else if (Comparer<T>.Default.Compare(TwoValue, OneValue) > 0)
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
    public static List<T> Sub<T>(this List<T> minuend, List<T> subtrahend)
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
    /// 从升序排序的两个List(每个List不含重复元素)，返回升序排序的并集。
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="one"></param>
    /// <param name="two"></param>
    /// <returns></returns>
    public static List<T> Union<T>(this List<T> one, List<T> two)
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
}
