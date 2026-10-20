using QuickPalette.Domain.Models;

namespace QuickPalette.Infrastructure.FileIndex;

/// <summary>
/// 把 USN 原始记录解析成完整路径的文件条目，并过滤系统目录。
/// 排除规则采用「路径段 + 卷根锚定」匹配：
///  - 路径段边界：规则按完整路径段比较，"windows" 不会误伤 "windows-app"；
///  - 卷根锚定：规则只在卷根起始的段序列上命中（&lt;root&gt;\windows\...），
///    D:\projects\windows\ 这类用户自己的同名目录不命中；
///  - 用户目录保护：&lt;root&gt;\users\&lt;user&gt;\... 一律不排除。
/// </summary>
public static class FileIndexBuilder
{
    /// <summary>
    /// 跳过的系统目录（用户要求：全盘索引但跳过系统目录）。
    /// 规则为"卷根相对的路径段序列"（首尾反斜杠仅为书写习惯，匹配时按段拆分）。
    /// </summary>
    public static readonly string[] ExcludedPathPrefixes =
    {
        @"\windows\",
        @"\windowsapps\",
        @"\program files\windowsapps\",
        @"\$recycle.bin\",
        @"\system volume information\",
        @"\recovery\",
        @"\programdata\microsoft\",
        @"\programdata\package cache\",
    };

    /// <summary>用户目录保护段：&lt;卷根&gt;\users\&lt;用户&gt;\ 以下一律保留</summary>
    public const string UserProfilesSegment = "users";

    /// <summary>NTFS 卷根目录的记录号（FRN 低 48 位恒为 5）</summary>
    public const long VolumeRootRecordNumber = 5;

    /// <summary>规则段序列（静态解析一次）</summary>
    private static readonly string[][] ExcludedSegments = ExcludedPathPrefixes.Select(ParseSegments).ToArray();

    /// <summary>
    /// 排除规则的 UTF-8 段字节（供节点级零字符串判定逐字节比较）。
    /// 与 <see cref="ExcludedSegments"/> 同源，规则语义只维护一份。
    /// </summary>
    public static readonly byte[][][] ExcludedSegmentsUtf8 =
        ExcludedSegments.Select(rule => rule.Select(s => System.Text.Encoding.UTF8.GetBytes(s)).ToArray()).ToArray();

    /// <summary>
    /// 归一化文件引用号：NTFS 的 FRN 高 16 位是序列号、低 48 位才是 MFT 记录号。
    /// 索引/查找一律用记录号做键，避免同一目录在"带序列号引用"与"裸记录号"两种形态下断链。
    /// </summary>
    public static long NormalizeFrn(long frn) => frn & 0x0000FFFFFFFFFFFFL;

    /// <summary>
    /// 由全量枚举记录构建<b>节点级</b>原始条目（Worker 重建用，内存优化版）：
    /// 与 <see cref="BuildFromEnumeration"/> 同一套去重语义（删除优先/最新记录胜出/忽略卷根），
    /// 但不构建 FileEntry / 路径缓存 / 完整路径（214 万条 ≈ 0.5GB 瞬态），
    /// 排除规则改由 InMemoryFileIndex.FilterExcluded 沿父链判定。
    /// </summary>
    public static List<RawEntry> BuildRawFromEnumeration(List<UsnRecord> records)
    {
        var deletedFrns = new HashSet<long>();
        var raw = new Dictionary<long, (long Parent, string Name, long Time, uint Attr)>(records.Count);
        foreach (var r in records)
        {
            var frn = NormalizeFrn(r.FileReference);
            if (frn == VolumeRootRecordNumber) continue; // 卷根记录忽略

            if ((r.Reason & NativeMethods.UsnReasonFileDelete) != 0)
            {
                deletedFrns.Add(frn);
                continue;
            }

            var ticks = NativeMethods.SafeDateTimeToFileTimeUtc(r.TimeStamp); // 坏时间单条防护
            if (raw.TryGetValue(frn, out var cur))
            {
                if (ticks <= cur.Time) continue; // 保留时间最新的记录（同刻保留先到者）
            }
            raw[frn] = (NormalizeFrn(r.ParentReference), r.FileName, ticks, r.FileAttributes);
        }

        foreach (var frn in deletedFrns) raw.Remove(frn);

        var result = new List<RawEntry>(raw.Count);
        foreach (var (frn, node) in raw)
        {
            long ticks;
            try
            {
                ticks = DateTime.FromFileTimeUtc(node.Time).Ticks;
            }
            catch
            {
                ticks = DateTime.UtcNow.Ticks; // 坏时间单条防护
            }
            result.Add(new RawEntry(
                node.Name, 0, frn, node.Parent, ticks,
                (node.Attr & NativeMethods.FileAttributeDirectory) != 0));
        }
        return result;
    }

    /// <summary>
    /// 由全量枚举记录构建文件条目列表
    /// </summary>
    public static List<FileEntry> BuildFromEnumeration(List<UsnRecord> records, string rootPath)
    {
        // FRN(记录号) → (父记录号, 名称, 大小, 时间, 属性)
        // USN 日志对同一 FRN 可能有多条变更记录（创建/改名/移动/删除）：
        //  - 任一记录带 FILE_DELETE → 该 FRN 整体丢弃（文件已不存在；
        //    不能"仅跳过删除记录"，否则同 FRN 的旧创建记录会被误当成活文件）；
        //  - 其余 FRN 只保留（时间, USN）最新的记录（USN 单调递增，用于同刻并列裁决）；
        //  - 卷根（记录号 5）的记录不是业务文件，直接忽略（防自环父引用/误产出根条目）。
        var deletedFrns = new HashSet<long>();
        var raw = new Dictionary<long, (long Parent, string Name, long Size, long Time, uint Attr)>(records.Count);
        foreach (var r in records)
        {
            var frn = NormalizeFrn(r.FileReference);
            if (frn == VolumeRootRecordNumber) continue; // 卷根记录忽略

            if ((r.Reason & NativeMethods.UsnReasonFileDelete) != 0)
            {
                deletedFrns.Add(frn);
                continue;
            }

            var ticks = NativeMethods.SafeDateTimeToFileTimeUtc(r.TimeStamp); // 坏时间单条防护
            if (raw.TryGetValue(frn, out var cur))
            {
                if (ticks <= cur.Time) continue; // 保留时间最新的记录（同刻保留先到者，USN 日志顺序即文件序）
            }
            raw[frn] = (NormalizeFrn(r.ParentReference), r.FileName, 0, ticks, r.FileAttributes);
        }

        // 存在删除记录的 FRN 整体丢弃
        foreach (var frn in deletedFrns) raw.Remove(frn);

        // 卷根目录（FRN=5）不一定在 USN 日志中（日志回绕会清除旧记录，$MFT 元数据区记录均无 USN）。
        // 不补齐它会导致所有文件的父链在卷根处断裂 → 全部解析失败。补一条即可让父链正确终止。
        raw.TryAdd(VolumeRootRecordNumber,
            (VolumeRootRecordNumber, "", 0, DateTime.UtcNow.ToFileTimeUtc(), NativeMethods.FileAttributeDirectory));

        var pathCache = new Dictionary<long, string?>(raw.Count);
        var result = new List<FileEntry>(records.Count);

        foreach (var frn in raw.Keys)
        {
            // 卷根自身与空名节点不是可检索条目
            if (frn == VolumeRootRecordNumber) continue;
            var (parent, name, _, time, attr) = raw[frn];
            if (name.Length == 0) continue;

            var path = ResolvePath(frn, raw, pathCache, rootPath);
            if (path == null) continue;

            if (IsExcluded(path, name, attr)) continue;

            var isDir = (attr & NativeMethods.FileAttributeDirectory) != 0;

            result.Add(new FileEntry
            {
                Name = name,
                Path = path,
                // 字段语义：USN 记录不含文件大小（大小在 MFT $STANDARD_INFORMATION 才有），
                // Size 恒为 0，UI 以修改时间展示；Modified 为 USN 记录时间（UTC）
                Size = 0,
                Modified = DateTime.FromFileTimeUtc(time),
                IsDirectory = isDir,
                FileReference = frn,        // 记录号（已归一化，去掉高 16 位序列号）
                ParentReference = parent    // 父目录记录号（同上）
            });
        }

        return result;
    }

    /// <summary>
    /// 判断路径是否命中排除规则（路径段 + 卷根锚定 + 用户目录保护，见类注释）。
    /// path 形如 "C:\windows\system32\notepad.exe"（有无尾斜杠皆可）。
    /// </summary>
    public static bool IsExcluded(string path, string name, uint attr)
        => IsExcludedSegments(ParseSegments(path), name, attr);

    /// <summary>
    /// 段级排除判定（单一事实源）：segments 为卷根之后的路径段（含条目自身名，根→叶顺序）。
    /// 供零路径构造的节点级落盘沿 FRN 父链复用同一套语义。
    /// </summary>
    public static bool IsExcludedSegments(IReadOnlyList<string> segments, string name, uint attr)
    {
        // NTFS 元文件（$MFT 等）：系统+隐藏
        if (name.Length > 0 && name[0] == '$' && (attr & NativeMethods.FileAttributeSystem) != 0)
            return true;

        if (segments.Count == 0) return false;

        // 用户目录保护：<卷根>\users\<用户>\... 一律保留（用户目录绝不受系统目录规则误伤）
        if (segments.Count >= 2 &&
            segments[0].Equals(UserProfilesSegment, StringComparison.OrdinalIgnoreCase))
            return false;

        // 卷根锚定：规则段序列必须从卷根（段 0）开始完整命中
        foreach (var rule in ExcludedSegments)
        {
            if (rule.Length == 0 || segments.Count < rule.Length) continue;
            var hit = true;
            for (var i = 0; i < rule.Length; i++)
            {
                if (!segments[i].Equals(rule[i], StringComparison.OrdinalIgnoreCase))
                {
                    hit = false;
                    break;
                }
            }
            if (hit) return true;
        }
        return false;
    }

    /// <summary>
    /// 拆分路径为卷根之后的路径段（去盘符/前导分隔符，忽略空段；正斜杠视同反斜杠）。
    /// 例："C:\windows\system32\" → ["windows", "system32"]
    /// </summary>
    private static string[] ParseSegments(string path)
    {
        if (string.IsNullOrEmpty(path)) return Array.Empty<string>();

        var normalized = path.Replace('/', '\\');
        var start = 0;
        // 跳过盘符前缀 "X:"（如有）
        if (normalized.Length >= 2 && normalized[1] == ':') start = 2;

        return normalized[start..]
            .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// 从 FRN 解析完整路径（迭代 + 记忆化）
    /// </summary>
    private static string? ResolvePath(
        long frn,
        Dictionary<long, (long Parent, string Name, long Size, long Time, uint Attr)> raw,
        Dictionary<long, string?> cache,
        string rootPath)
    {
        if (cache.TryGetValue(frn, out var cached)) return cached;

        // 沿父链向上收集，直到命中缓存、卷根（FRN=5）或断链
        var chain = new List<long>();
        var cur = frn;
        while (true)
        {
            if (cache.TryGetValue(cur, out cached))
            {
                // 从缓存起点回推
                var path = cached;
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    if (path == null) break;
                    var (_, name, _, _, _) = raw[chain[i]];
                    path = path.EndsWith('\\') ? path + name : path + "\\" + name;
                    cache.TryAdd(chain[i], path);
                }
                return path;
            }

            // 卷根判定：父引用可能带序列号（如 0x0005000000000005），
            // 且根目录记录本身可能不在 USN 日志中（日志回绕清除）——
            // 比较记录号（FRN 低 48 位）而不是完整值。
            if (NormalizeFrn(cur) == VolumeRootRecordNumber)
            {
                var root = rootPath.EndsWith('\\') ? rootPath : rootPath + "\\";
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    var (_, name, _, _, _) = raw[chain[i]];
                    // 逐级拼接路径，每级之间必须有分隔符
                    root = root.EndsWith('\\') ? root + name : root + "\\" + name;
                    cache.TryAdd(chain[i], root);
                }
                cache[NormalizeFrn(cur)] = rootPath.EndsWith('\\') ? rootPath : rootPath + "\\"; // 根引用的路径恒为卷根
                cache[frn] = root;
                return root;
            }

            if (!raw.TryGetValue(cur, out var node) || chain.Count > 256) break;
            if (node.Parent == cur) break; // 自环，防御

            chain.Add(cur);
            cur = node.Parent;
        }

        // 断链：标记缓存，避免反复解析
        cache[frn] = null;
        return null;
    }
}
