using System.Runtime.InteropServices;
using System.Text;
using QuickPalette.Infrastructure.FileIndex;

// USN Journal 诊断工具：逐步打印每个 DeviceIoControl 调用的结果、错误码与原始字节
// 用法：UsnDiag.exe [日志路径] [卷根]
//   日志路径缺省 = %TEMP%\usndiag.log（禁止硬编码用户目录）
//   卷根缺省 = C:\
class Program
{
    private const uint FsctlQueryUsnJournal = 0x000900f4;
    private const uint FsctlCreateUsnJournal = 0x000900e7;
    private const uint FsctlEnumUsnData = 0x000900b3;
    private const uint FsctlReadUsnJournal = 0x000900bb;
    private const int ErrorHandleEof = 38;

    // READ_USN_JOURNAL_DATA_V0.Timeout 单位是 100 纳秒（官方语义）：等待下一条记录的最小时间。
    // 1000ms = 1000 * 10,000 = 10,000,000 个单位 —— 绝不能把毫秒数直接当单位传入。
    private const long Timeout100NsFor1Second = 1000L * 10_000L;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr h, uint code, byte[]? inb, uint insz, byte[] outb, uint outsz, out uint ret, IntPtr ov);

    private static void Main(string[] args)
    {
        // 日志路径：命令行参数优先，缺省 %TEMP%\usndiag.log
        var logPath = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0]
            : Path.Combine(Path.GetTempPath(), "usndiag.log");
        var volumeRoot = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1] : "C:\\";

        using var w = new StreamWriter(logPath, false, Encoding.UTF8) { AutoFlush = true };

        w.WriteLine($"=== UsnDiag {DateTime.Now:HH:mm:ss} (log={logPath}, volume={volumeRoot}) ===");
        var device = @"\\.\" + volumeRoot.TrimEnd('\\').TrimEnd(':') + ":";
        var h = CreateFile(device, 0x80000000 | 0x40000000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        w.WriteLine($"OpenVolume {device} handle={h} (invalid={(h == new IntPtr(-1))})");
        if (h == new IntPtr(-1))
        {
            w.WriteLine($"CreateFile error={Marshal.GetLastWin32Error()}");
            return;
        }

        // 1. QUERY
        var qout = new byte[256];
        var okQ = DeviceIoControl(h, FsctlQueryUsnJournal, null, 0, qout, (uint)qout.Length, out var qret, IntPtr.Zero);
        w.WriteLine($"QUERY ok={okQ} ret={qret} err={(okQ ? 0 : Marshal.GetLastWin32Error())}");
        if (okQ)
        {
            w.WriteLine("QUERY bytes: " + Hex(qout, (int)Math.Min(qret, 56)));
            var journalId = BitConverter.ToUInt64(qout, 0);
            var nextUsn = BitConverter.ToInt64(qout, 16);
            w.WriteLine($"QUERY parsed: journalId={journalId} firstUsn={BitConverter.ToInt64(qout, 8)} nextUsn={nextUsn}");

            // 2b. ENUM 完整循环（复刻产品 EnumerateAll：skip 8 + 起点推进）
            {
                var eoutF = new byte[1 << 20];
                var mftF = new byte[24];
                Array.Fill<byte>(mftF, 0);
                mftF[16] = 0xFF; mftF[17] = 0xFF; mftF[18] = 0xFF; mftF[19] = 0xFF; mftF[20] = 0xFF; mftF[21] = 0xFF; mftF[22] = 0xFF; mftF[23] = 0x7F;
                ulong lastStart = 0;
                var totalRecords = 0;
                var batchCount = 0;
                while (true)
                {
                    var ok = DeviceIoControl(h, FsctlEnumUsnData, mftF, 24, eoutF, (uint)eoutF.Length, out var ret, IntPtr.Zero);
                    if (!ok)
                    {
                        w.WriteLine($"ENUM batch err={(ok ? 0 : Marshal.GetLastWin32Error())}");
                        break;
                    }
                    if (ret <= 8) { w.WriteLine($"ENUM batch ret={ret} <= 8, done"); break; }
                    var next = BitConverter.ToUInt64(eoutF, 0);
                    var parsed = ParseRecordsFull(eoutF, (int)ret, 8);
                    totalRecords += parsed;
                    batchCount++;
                    w.WriteLine($"ENUM batch#{batchCount}: ret={ret} nextStart={next} parsed={parsed} first8={Hex(eoutF, 16)}");
                    if (next <= lastStart) { w.WriteLine("  nextStart not advancing, done"); break; }
                    lastStart = next;
                    BitConverter.GetBytes(next).CopyTo(mftF, 0);
                }
                w.WriteLine($"ENUM total: batches={batchCount} records={totalRecords}");
            }

            // 4. 真实产品代码路径验证（EnumerateAll + BuildFromEnumeration）
            {
                using var volHandle = UsnVolumeReader.OpenVolume(volumeRoot);
                if (volHandle == null)
                {
                    w.WriteLine("PRODUCT OpenVolume returned null (只读句柄打开失败)");
                }
                else
                {
                    var result = UsnVolumeReader.EnumerateAll(volHandle);
                    var records = result.Records;
                    w.WriteLine($"PRODUCT EnumerateAll: {records.Count} records, supported={result.IsSupported}, warning={result.Warning ?? "-"}");
                    if (!result.IsSupported)
                        w.WriteLine($"  unsupported versions: {string.Join(",", result.UnsupportedVersions)}");
                    var built = FileIndexBuilder.BuildFromEnumeration(records, volumeRoot);
                    w.WriteLine($"PRODUCT BuildFromEnumeration: {built.Count} entries");
                    w.WriteLine($"has FRN5(root): {records.Any(r => FileIndexBuilder.NormalizeFrn(r.FileReference) == 5)}");
                    // 缺失父链统计（除卷根 5 外）
                    var have = new HashSet<long>(records.Select(r => FileIndexBuilder.NormalizeFrn(r.FileReference)));
                    var missingParents = records
                        .Select(r => FileIndexBuilder.NormalizeFrn(r.ParentReference))
                        .Where(p => p != 5 && !have.Contains(p))
                        .Distinct()
                        .ToList();
                    w.WriteLine($"missing parent FRNs (excluding root): {missingParents.Count}");
                    foreach (var r in records.Take(3))
                        w.WriteLine($"  sample: FRN={r.FileReference} name=\"{r.FileName}\"");
                    foreach (var e in built.Take(5))
                        w.WriteLine($"  built sample: {e.Path}");
                }
            }

            // 3. READ — StartUsn=0, 全 ReasonMask, journalId 用 QUERY 结果
            // READ_USN_JOURNAL_DATA_V0 布局：StartUsn(0) ReasonMask(8) ReturnOnlyOnClose(12)
            //   Timeout(16,100ns) BytesToWaitFor(24) UsnJournalId(32) — 共 40 字节
            var rd = new byte[40];
            Array.Fill<byte>(rd, 0);
            rd[8] = 0xFF; rd[9] = 0xFF; rd[10] = 0xFF; rd[11] = 0xFF;          // ReasonMask
            // Timeout（偏移 16，100ns 单位）：1 秒 = 10,000,000
            BitConverter.GetBytes(Timeout100NsFor1Second).CopyTo(rd, 16);
            rd[24] = 0x01;                                                     // BytesToWaitFor = 1
            BitConverter.GetBytes(journalId).CopyTo(rd, 32);
            var rout = new byte[1 << 20];
            var okR = DeviceIoControl(h, FsctlReadUsnJournal, rd, 40, rout, (uint)rout.Length, out var rret, IntPtr.Zero);
            w.WriteLine($"READ(manual,start=0) ok={okR} ret={rret} err={(okR ? 0 : Marshal.GetLastWin32Error())}");
            if (okR && rret >= 8)
            {
                w.WriteLine("READ nextUsn=" + BitConverter.ToInt64(rout, 0) + " bytes: " + Hex(rout, (int)Math.Min(rret, 32)));
            }

            // 4. ENUM — 用 Marshal StructToBytes（复现现有实现）
            var mftStruct = new MftEnumDataV0 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = long.MaxValue };
            var mftBytes = StructToBytes(mftStruct);
            w.WriteLine("ENUM Marshal in-buffer: " + Hex(mftBytes, 24));
            var eout2 = new byte[1 << 20];
            var okE2 = DeviceIoControl(h, FsctlEnumUsnData, mftBytes, (uint)mftBytes.Length, eout2, (uint)eout2.Length, out var eret2, IntPtr.Zero);
            w.WriteLine($"ENUM(marshal) ok={okE2} ret={eret2} err={(okE2 ? 0 : Marshal.GetLastWin32Error())}");
            if (okE2 && eret2 > 0)
            {
                w.WriteLine("ENUM(marshal) first 80 bytes: " + Hex(eout2, 80));
            }
        }

        w.WriteLine("=== done ===");
    }

    private static string Hex(byte[] buf, int n)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < n && i < buf.Length; i++)
        {
            sb.Append(buf[i].ToString("X2"));
            if (i % 8 == 7) sb.Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>从 startOffset 解析 USN_RECORD（V2/V3），返回条数</summary>
    private static int ParseRecordsFull(byte[] buf, int length, int startOffset)
    {
        var offset = startOffset;
        var count = 0;
        while (offset + 8 <= length)
        {
            var recordLength = BitConverter.ToUInt32(buf, offset);
            if (recordLength < 60 || offset + recordLength > length) break;
            var major = BitConverter.ToUInt16(buf, offset + 4);
            if (major == 2 || major == 3) count++;
            offset += (int)recordLength;
        }
        return count;
    }

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var arr = new byte[size];
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            Marshal.Copy(ptr, arr, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return arr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftEnumDataV0
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }
}
