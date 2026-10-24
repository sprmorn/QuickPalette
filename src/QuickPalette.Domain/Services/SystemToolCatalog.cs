namespace QuickPalette.Domain.Services;

using QuickPalette.Domain.Models;

/// <summary>
/// Windows 系统组件内置目录：系统工具、控制面板项、MMC 管理控制台、
/// ms-settings 设置页、Windows 安全中心深链、系统特殊文件夹。静态只读数据，
/// 供 <see cref="SystemToolSearch"/> 检索直达。
///
/// 约定：
///  - Name 为微软官方简体中文名（全目录唯一）；NameEn 为英文名；
///  - Launch 为启动命令：URI（ms-settings:/windowsdefender:）或命令行（支持 %SystemRoot% 等环境变量），
///    裸命令名由 UI 层按 System32 → Windows → PATH 解析；
///  - RequiresAdmin 按可执行文件清单实测标注（regedit/msconfig=requireAdministrator；
///    mmc.exe=highestAvailable，故全部 .msc 控制台会触发 UAC；control.exe/.cpl=asInvoker）；
///  - Keywords 收录中文俗称与英文命令名（小写）；个别未逐一实测的条目在 Keywords 中带 "unverified" 标记，
///    本机不存在的条目（如家庭版无 gpedit.msc、未装 RSAT）由 UI 层可用性过滤剔除；
///  - 拼音由 <see cref="PinyinProvider"/> 按 Name 现算。
/// </summary>
public static class SystemToolCatalog
{
    /// <summary>全部系统组件条目</summary>
    public static IReadOnlyList<SystemTool> Tools { get; } = Build();

    private static List<SystemTool> Build()
    {
        var tools = new List<SystemTool>(256);        Tool(tools, "regedit", "注册表编辑器", "Registry Editor", "regedit", true, "注册表", "regedit", "registry", "reg");
        Tool(tools, "taskmgr", "任务管理器", "Task Manager", "taskmgr", false, "任务管理器", "taskmgr", "进程", "性能", "process");
        Tool(tools, "msconfig", "系统配置", "System Configuration", "msconfig", true, "系统配置", "msconfig", "启动项", "引导", "boot");
        Tool(tools, "resmon", "资源监视器", "Resource Monitor", "resmon", false, "资源监视器", "resmon", "resource monitor", "监视");
        Tool(tools, "perfmon", "可靠性监视器", "Reliability Monitor", "perfmon /rel", false, "可靠性监视器", "reliability monitor", "稳定性", "perfmon /rel");
        Tool(tools, "dxdiag", "DirectX 诊断工具", "DirectX Diagnostic Tool", "dxdiag", false, "dxdiag", "directx", "诊断", "显卡", "声音");
        Tool(tools, "msinfo32", "系统信息", "System Information", "msinfo32", false, "系统信息", "msinfo32", "system information", "硬件信息");
        Tool(tools, "charmap", "字符映射表", "Character Map", "charmap", false, "字符映射表", "charmap", "特殊符号", "字符");
        Tool(tools, "osk", "屏幕键盘", "On-Screen Keyboard", "osk", false, "屏幕键盘", "osk", "虚拟键盘", "on-screen keyboard");
        Tool(tools, "magnify", "放大镜", "Magnifier", "magnify", false, "放大镜", "magnify", "放大", "屏幕放大");
        Tool(tools, "narrator", "讲述人", "Narrator", "narrator", false, "讲述人", "narrator", "朗读", "屏幕阅读器");
        Tool(tools, "snippingtool", "截图工具", "Snipping Tool", "snippingtool", false, "截图工具", "snippingtool", "截图", "snip", "截图和草图");
        Tool(tools, "mspaint", "画图", "Paint", "mspaint", false, "画图", "mspaint", "画板", "paint");
        Tool(tools, "notepad", "记事本", "Notepad", "notepad", false, "记事本", "notepad", "文本", "txt");
        Tool(tools, "calc", "计算器", "Calculator", "calc", false, "计算器", "calc", "calculator");
        Tool(tools, "psr", "步骤记录器", "Steps Recorder", "psr", false, "步骤记录器", "psr", "问题步骤记录器", "problem steps recorder");
        Tool(tools, "powershell", "Windows PowerShell", "Windows PowerShell", "powershell", false, "powershell", "命令行", "shell", "脚本");
        Tool(tools, "powershell_ise", "Windows PowerShell ISE", "Windows PowerShell ISE", "powershell_ise", false, "powershell ise", "ise", "脚本编辑器", "unverified");
        Tool(tools, "wt", "Windows 终端", "Windows Terminal", "wt", false, "终端", "terminal", "wt", "windows terminal", "unverified");
        Tool(tools, "cmd", "命令提示符", "Command Prompt", "cmd", false, "命令提示符", "cmd", "dos", "控制台");
        Tool(tools, "mstsc", "远程桌面连接", "Remote Desktop Connection", "mstsc", false, "远程桌面", "mstsc", "rdp", "远程连接");
        Tool(tools, "wordpad", "写字板", "WordPad", "wordpad", false, "写字板", "wordpad", "rtf", "unverified");
        Tool(tools, "file-explorer", "文件资源管理器", "File Explorer", "explorer", false, "资源管理器", "explorer", "文件管理器", "文件夹");
        Tool(tools, "winver", "关于 Windows", "About Windows", "winver", false, "winver", "关于windows", "版本号", "windows版本");
        Tool(tools, "cleanmgr", "磁盘清理", "Disk Cleanup", "cleanmgr", false, "磁盘清理", "cleanmgr", "清理", "垃圾清理");
        Tool(tools, "dfrgui", "碎片整理和优化驱动器", "Defragment and Optimize Drives", "dfrgui", true, "磁盘碎片整理", "dfrgui", "优化驱动器", "defrag");
        Tool(tools, "displayswitch", "投影（切换显示器）", "Project (Display Switch)", "displayswitch", false, "投影", "displayswitch", "第二屏幕", "切换显示器", "扩展屏幕");
        Tool(tools, "mblctr", "Windows 移动中心", "Windows Mobility Center", "mblctr", false, "移动中心", "mblctr", "mobility center");
        Tool(tools, "fsquirt", "蓝牙文件传输", "Bluetooth File Transfer", "fsquirt", false, "蓝牙文件传输", "fsquirt", "蓝牙", "bluetooth");
        Tool(tools, "eudcedit", "专用字符编辑器", "Private Character Editor", "eudcedit", true, "专用字符编辑器", "eudcedit", "造字", "私人字符");
        Tool(tools, "slui", "Windows 激活", "Windows Activation", "slui", false, "激活", "slui", "windows激活", "产品密钥");
        Cpl(tools, "control", "控制面板", "Control Panel", "control.exe", false, "控制面板", "control", "control panel");
        Cpl(tools, "appwiz", "程序和功能", "Programs and Features", "appwiz.cpl", false, "程序和功能", "appwiz", "卸载", "添加或删除程序", "uninstall");
        Cpl(tools, "sysdm", "系统属性", "System Properties", "sysdm.cpl", false, "系统属性", "sysdm", "计算机名", "硬件", "远程", "环境变量");
        Cpl(tools, "ncpa", "网络连接", "Network Connections", "ncpa.cpl", false, "网络连接", "ncpa", "适配器", "网卡", "本地连接");
        Cpl(tools, "cp-networkandsharingcenter", "网络和共享中心", "Network and Sharing Center", "control.exe /name Microsoft.NetworkAndSharingCenter", false, "网络和共享中心", "netcenter", "共享", "网络状态");
        Cpl(tools, "main", "鼠标", "Mouse", "main.cpl", false, "鼠标", "main.cpl", "指针", "鼠标键");
        Cpl(tools, "cp-keyboard", "键盘", "Keyboard", "control.exe /name Microsoft.Keyboard", false, "键盘", "keyboard", "按键", "重复延迟");
        Cpl(tools, "mmsys", "声音（经典）", "Sound", "mmsys.cpl", false, "声音", "mmsys", "音频", "播放设备", "录制");
        Cpl(tools, "powercfg", "电源选项", "Power Options", "powercfg.cpl", false, "电源选项", "powercfg", "电源计划", "休眠", "睡眠");
        Cpl(tools, "intl", "区域（经典）", "Region", "intl.cpl", false, "区域", "intl", "格式", "数字格式", "日期格式");
        Cpl(tools, "timedate", "日期和时间（经典）", "Date and Time", "timedate.cpl", false, "日期和时间", "timedate", "时区", "时间服务器");
        Cpl(tools, "firewall", "Windows Defender 防火墙", "Windows Defender Firewall", "firewall.cpl", false, "防火墙", "firewall", "windows defender 防火墙", "入站规则");
        Cpl(tools, "bthprops", "蓝牙设备", "Bluetooth Devices", "bthprops.cpl", false, "蓝牙", "bthprops", "bluetooth", "蓝牙设置", "unverified");
        Cpl(tools, "cp-easeofaccesscenter", "轻松使用设置中心", "Ease of Access Center", "control.exe /name Microsoft.EaseOfAccessCenter", false, "轻松使用", "ease of access", "辅助功能", "accessibility");
        Cpl(tools, "cp-useraccounts", "用户账户", "User Accounts", "control.exe /name Microsoft.UserAccounts", false, "用户账户", "user accounts", "账户管理", "usercpl");
        Cpl(tools, "netplwiz", "用户账户（高级）", "User Accounts (Advanced)", "netplwiz", true, "netplwiz", "userpasswords2", "用户账户", "自动登录", "高级用户账户");
        Cpl(tools, "cp-credentialmanager", "凭据管理器", "Credential Manager", "control.exe /name Microsoft.CredentialManager", false, "凭据管理器", "credential manager", "凭据", "windows凭据");
        Cpl(tools, "cp-indexingoptions", "索引选项", "Indexing Options", "control.exe /name Microsoft.IndexingOptions", false, "索引选项", "indexing", "搜索索引", "重建索引");
        Cpl(tools, "cp-devicesandprinters", "设备和打印机", "Devices and Printers", "control.exe /name Microsoft.DevicesAndPrinters", false, "设备和打印机", "devices and printers", "打印机", "设备");
        Cpl(tools, "cp-bitlockerdriveencryption", "BitLocker 驱动器加密", "BitLocker Drive Encryption", "control.exe /name Microsoft.BitLockerDriveEncryption", true, "bitlocker", "驱动器加密", "磁盘加密", "fvecpl");
        Cpl(tools, "cp-filehistory", "文件历史记录", "File History", "control.exe /name Microsoft.FileHistory", false, "文件历史记录", "file history", "备份", "还原文件");
        Cpl(tools, "cp-folderoptions", "文件夹选项", "Folder Options", "control.exe /name Microsoft.FolderOptions", false, "文件夹选项", "folder options", "显示隐藏文件", "文件扩展名", "查看");
        Cpl(tools, "cp-internetoptions", "Internet 选项", "Internet Options", "control.exe /name Microsoft.InternetOptions", false, "internet选项", "inetcpl.cpl", "ie选项", "代理", "安全级别");
        Cpl(tools, "cp-recovery", "恢复（控制面板）", "Recovery", "control.exe /name Microsoft.Recovery", false, "恢复", "recovery", "创建恢复驱动器", "系统还原");
        Cpl(tools, "cp-speechrecognition", "语音识别", "Speech Recognition", "control.exe /name Microsoft.SpeechRecognition", false, "语音识别", "speech recognition", "语音控制");
        Cpl(tools, "cp-synccenter", "同步中心", "Sync Center", "control.exe /name Microsoft.SyncCenter", false, "同步中心", "sync center", "脱机文件", "mobsync");
        Cpl(tools, "cp-colormanagement", "颜色管理", "Color Management", "control.exe /name Microsoft.ColorManagement", false, "颜色管理", "colorcpl", "色彩管理", "icc", "校色");
        Cpl(tools, "cp-storagespaces", "存储空间", "Storage Spaces", "control.exe /name Microsoft.StorageSpaces", true, "存储空间", "storage spaces", "磁盘阵列", "pool");
        Cpl(tools, "iscsicpl", "iSCSI 发起程序", "iSCSI Initiator", "iscsicpl", true, "iscsi", "iscsicpl", "iscsi发起程序", "存储网络");
        Cpl(tools, "cp-actioncenter", "安全和维护", "Security and Maintenance", "control.exe /name Microsoft.ActionCenter", false, "安全和维护", "操作中心", "action center", "wscui.cpl", "安全中心");
        Cpl(tools, "cp-administrativetools", "管理工具", "Administrative Tools", "control.exe /name Microsoft.AdministrativeTools", false, "管理工具", "administrative tools", "管理");
        Cpl(tools, "sdclt", "备份和还原（Windows 7）", "Backup and Restore (Windows 7)", "sdclt", true, "备份和还原", "sdclt", "windows 7 备份", "系统映像");
        Msc(tools, "devmgmt", "设备管理器", "Device Manager", "devmgmt.msc", true, "设备管理器", "devmgmt", "device manager", "驱动", "驱动程序");
        Msc(tools, "services", "服务", "Services", "services.msc", true, "服务", "services", "系统服务", "service", "后台服务");
        Msc(tools, "compmgmt", "计算机管理", "Computer Management", "compmgmt.msc", true, "计算机管理", "compmgmt", "computer management", "管理");
        Msc(tools, "diskmgmt", "磁盘管理", "Disk Management", "diskmgmt.msc", true, "磁盘管理", "diskmgmt", "disk management", "分区", "格式化", "卷");
        Msc(tools, "eventvwr", "事件查看器", "Event Viewer", "eventvwr.msc", true, "事件查看器", "eventvwr", "事件日志", "log", "系统日志");
        Msc(tools, "perfmon-2", "性能监视器", "Performance Monitor", "perfmon.msc", true, "性能监视器", "perfmon", "performance monitor", "性能计数器");
        Msc(tools, "gpedit", "本地组策略编辑器", "Local Group Policy Editor", "gpedit.msc", true, "组策略", "gpedit", "group policy", "本地组策略", "unverified");
        Msc(tools, "secpol", "本地安全策略", "Local Security Policy", "secpol.msc", true, "本地安全策略", "secpol", "security policy", "安全设置", "unverified");
        Msc(tools, "taskschd", "任务计划程序", "Task Scheduler", "taskschd.msc", true, "任务计划程序", "taskschd", "计划任务", "task scheduler", "定时任务");
        Msc(tools, "wf", "Windows Defender 防火墙与高级安全", "Windows Defender Firewall with Advanced Security", "wf.msc", true, "防火墙", "wf.msc", "高级安全", "入站规则", "出站规则");
        Msc(tools, "certmgr", "证书（当前用户）", "Certificates (Current User)", "certmgr.msc", true, "证书", "certmgr", "证书管理", "当前用户", "个人证书");
        Msc(tools, "certlm", "证书（本地计算机）", "Certificates (Local Computer)", "certlm.msc", true, "证书", "certlm", "本地计算机", "计算机证书", "受信任的根证书");
        Msc(tools, "printmanagement", "打印管理", "Print Management", "printmanagement.msc", true, "打印管理", "printmanagement", "打印机管理", "打印队列", "unverified");
        Msc(tools, "wmimgmt", "WMI 控制", "WMI Control", "wmimgmt.msc", true, "wmi", "wmimgmt", "wmi控制", "wmimgmt.msc");
        Msc(tools, "fsmgmt", "共享文件夹", "Shared Folders", "fsmgmt.msc", true, "共享文件夹", "fsmgmt", "共享管理", "会话", "打开文件");
        Msc(tools, "lusrmgr", "本地用户和组", "Local Users and Groups", "lusrmgr.msc", true, "本地用户和组", "lusrmgr", "用户管理", "用户和组", "密码");
        Msc(tools, "mmc", "空白控制台", "Empty MMC Console", "mmc", true, "mmc", "控制台", "空白控制台", "自定义控制台");
        Msc(tools, "comexp", "组件服务", "Component Services", "comexp.msc", true, "组件服务", "comexp", "com+", "dcom", "组件服务", "unverified");
        Msc(tools, "tpm", "TPM 管理", "TPM Management", "tpm.msc", true, "tpm", "安全芯片", "受信任的平台模块", "bitlocker", "unverified");
        Msc(tools, "azman", "授权管理器", "Authorization Manager", "azman.msc", true, "授权管理器", "azman", "authorization manager", "unverified");
        Msc(tools, "rsop", "策略的结果集", "Resultant Set of Policy", "rsop.msc", true, "策略结果集", "rsop", "组策略结果", "策略诊断");
        Msc(tools, "dnsmgmt", "DNS 管理器", "DNS Manager", "dnsmgmt.msc", true, "dns", "dnsmgmt", "dns管理器", "域名解析", "unverified");
        Msc(tools, "dhcpmgmt", "DHCP 管理", "DHCP Manager", "dhcpmgmt.msc", true, "dhcp", "dhcpmgmt", "dhcp管理", "地址分配", "unverified");
        Msc(tools, "winsmgmt", "WINS 管理", "WINS Manager", "winsmgmt.msc", true, "wins", "winsmgmt", "wins管理", "unverified");
        Msc(tools, "tapimgmt", "电话服务", "Telephony", "tapimgmt.msc", true, "电话", "tapi", "tapimgmt", "电话服务", "unverified");
        Msc(tools, "inetmgr", "IIS 管理器", "IIS Manager", "inetmgr", true, "iis", "inetmgr", "iis管理器", "网站管理", "web服务器", "unverified");
        Sys(tools, "settings", "设置", "Settings", "ms-settings:", false, "设置", "settings", "windows设置");
        Sys(tools, "set-display", "显示", "Display", "ms-settings:display", false, "显示", "display", "分辨率", "缩放", "多显示器");
        Sys(tools, "set-display-advanced", "高级显示设置", "Advanced display settings", "ms-settings:display-advanced", false, "高级显示", "刷新率", "display advanced", "显卡信息");
        Sys(tools, "set-sound", "声音", "Sound", "ms-settings:sound", false, "声音", "sound", "音量", "输出", "输入");
        Sys(tools, "set-apps-volume", "音量合成器", "Volume mixer", "ms-settings:apps-volume", false, "音量合成器", "volume mixer", "应用音量", "单独音量");
        Sys(tools, "set-notifications", "通知", "Notifications", "ms-settings:notifications", false, "通知", "notifications", "通知和操作", "横幅通知");
        Sys(tools, "set-quiethours", "专注助手", "Focus assist", "ms-settings:quiethours", false, "专注助手", "focus assist", "专注", "免打扰", "quiet hours");
        Sys(tools, "set-powersleep", "电源和睡眠", "Power & sleep", "ms-settings:powersleep", false, "电源和睡眠", "power sleep", "睡眠", "关屏时间");
        Sys(tools, "set-batterysaver", "节电模式", "Battery saver", "ms-settings:batterysaver", false, "节电模式", "battery saver", "电池", "省电");
        Sys(tools, "set-storagesense", "存储", "Storage", "ms-settings:storagesense", false, "存储", "storage", "磁盘空间", "存储感知");
        Sys(tools, "set-disksandvolumes", "磁盘和卷", "Disks & volumes", "ms-settings:disksandvolumes", false, "磁盘和卷", "disks and volumes", "磁盘", "卷", "unverified");
        Sys(tools, "set-multitasking", "多任务处理", "Multitasking", "ms-settings:multitasking", false, "多任务处理", "multitasking", "贴靠窗口", "分屏", "snap");
        Sys(tools, "set-activation", "激活", "Activation", "ms-settings:activation", false, "激活", "activation", "产品密钥", "许可证");
        Sys(tools, "set-findmydevice", "查找我的设备", "Find my device", "ms-settings:findmydevice", false, "查找我的设备", "find my device", "定位", "丢失");
        Sys(tools, "set-deviceencryption", "设备加密", "Device encryption", "ms-settings:deviceencryption", false, "设备加密", "device encryption", "bitlocker", "加密");
        Sys(tools, "set-project", "投影到此电脑", "Projecting to this PC", "ms-settings:project", false, "投影到此电脑", "project", "无线投影", "miracast");
        Sys(tools, "set-remotedesktop", "远程桌面", "Remote Desktop", "ms-settings:remotedesktop", false, "远程桌面", "remote desktop", "rdp", "远程连接");
        Sys(tools, "set-clipboard", "剪贴板", "Clipboard", "ms-settings:clipboard", false, "剪贴板", "clipboard", "剪贴板历史", "跨设备粘贴");
        Sys(tools, "set-nightlight", "夜间模式", "Night light", "ms-settings:nightlight", false, "夜间模式", "night light", "护眼", "蓝光");
        Sys(tools, "set-about", "关于", "About", "ms-settings:about", false, "关于", "about", "系统信息", "设备规格", "重命名电脑");
        Sys(tools, "set-troubleshoot", "疑难解答", "Troubleshoot", "ms-settings:troubleshoot", false, "疑难解答", "troubleshoot", "诊断", "修复");
        Sys(tools, "set-recovery", "恢复", "Recovery", "ms-settings:recovery", false, "恢复", "recovery", "重置此电脑", "高级启动", "系统还原");
        Sys(tools, "set-developers", "开发人员选项", "For developers", "ms-settings:developers", false, "开发人员选项", "developers", "开发人员模式", "旁加载");
        Sys(tools, "set-windowsinsider", "Windows 预览体验计划", "Windows Insider Program", "ms-settings:windowsinsider", false, "预览体验计划", "insider", "windows insider", "测试版");
        Sys(tools, "set-energyrecommendations", "能源建议", "Energy recommendations", "ms-settings:energyrecommendations", false, "能源建议", "energy recommendations", "节能", "unverified");
        Sys(tools, "set-network-status", "网络和 Internet", "Network & internet", "ms-settings:network-status", false, "网络", "network", "网络状态", "internet");
        Sys(tools, "set-network-wifi", "WLAN", "Wi-Fi", "ms-settings:network-wifi", false, "wlan", "wifi", "wi-fi", "无线网络", "无线");
        Sys(tools, "set-network-wifisettings", "管理已知网络", "Manage known networks", "ms-settings:network-wifisettings", false, "已知网络", "known networks", "wifi密码", "忘记网络");
        Sys(tools, "set-network-ethernet", "以太网", "Ethernet", "ms-settings:network-ethernet", false, "以太网", "ethernet", "有线网络", "网卡", "ip");
        Sys(tools, "set-network-vpn", "VPN", "VPN", "ms-settings:network-vpn", false, "vpn", "虚拟专用网络", "拨号vpn");
        Sys(tools, "set-network-proxy", "代理", "Proxy", "ms-settings:network-proxy", false, "代理", "proxy", "代理服务器", "pac");
        Sys(tools, "set-network-airplanemode", "飞行模式", "Airplane mode", "ms-settings:network-airplanemode", false, "飞行模式", "airplane mode", "飞行");
        Sys(tools, "set-network-mobilehotspot", "移动热点", "Mobile hotspot", "ms-settings:network-mobilehotspot", false, "移动热点", "mobile hotspot", "热点", "共享网络");
        Sys(tools, "set-network-advancedsettings", "高级网络设置", "Advanced network settings", "ms-settings:network-advancedsettings", false, "高级网络设置", "网络适配器", "网络重置", "unverified");
        Sys(tools, "set-bluetooth", "蓝牙和其他设备", "Bluetooth & other devices", "ms-settings:bluetooth", false, "蓝牙", "bluetooth", "蓝牙和设备", "设备", "配对");
        Sys(tools, "set-printers", "打印机和扫描仪", "Printers & scanners", "ms-settings:printers", false, "打印机", "printers", "扫描仪", "添加打印机");
        Sys(tools, "set-mousetouchpad", "鼠标和触摸板", "Mouse & touchpad", "ms-settings:mousetouchpad", false, "鼠标", "触摸板", "mouse", "touchpad", "unverified");
        Sys(tools, "set-devices-touchpad", "触摸板", "Touchpad", "ms-settings:devices-touchpad", false, "触摸板", "touchpad", "触控板", "手势", "unverified");
        Sys(tools, "set-typing", "输入", "Typing", "ms-settings:typing", false, "输入", "typing", "输入法设置", "自动更正", "文本建议");
        Sys(tools, "set-usb", "USB", "USB", "ms-settings:usb", false, "usb", "通知", "u盘", "连接");
        Sys(tools, "set-autoplay", "自动播放", "AutoPlay", "ms-settings:autoplay", false, "自动播放", "autoplay", "u盘", "光盘");
        Sys(tools, "set-pen", "笔和 Windows Ink", "Pen & Windows Ink", "ms-settings:pen", false, "笔", "windows ink", "手写笔", "触控笔", "unverified");
        Sys(tools, "set-mobile-devices", "手机连接", "Mobile devices", "ms-settings:mobile-devices", false, "手机连接", "phone link", "你的手机", "手机", "unverified");
        Sys(tools, "set-appsfeatures", "应用和功能", "Apps & features", "ms-settings:appsfeatures", false, "应用和功能", "apps features", "卸载", "已安装的应用", "应用");
        Sys(tools, "set-defaultapps", "默认应用", "Default apps", "ms-settings:defaultapps", false, "默认应用", "default apps", "默认程序", "文件关联");
        Sys(tools, "set-startupapps", "启动应用", "Startup apps", "ms-settings:startupapps", false, "启动应用", "startup apps", "开机启动", "自启动");
        Sys(tools, "set-optionalfeatures", "可选功能", "Optional features", "ms-settings:optionalfeatures", false, "可选功能", "optional features", "windows功能", "rsat");
        Sys(tools, "set-maps", "离线地图", "Offline maps", "ms-settings:maps", false, "离线地图", "offline maps", "地图", "下载地图");
        Sys(tools, "set-workplace", "访问工作或学校账户", "Access work or school", "ms-settings:workplace", false, "工作或学校", "work or school", "域", "azure ad", "unverified");
        Sys(tools, "set-emailandaccounts", "电子邮件和账户", "Email & accounts", "ms-settings:emailandaccounts", false, "电子邮件", "accounts", "账户", "邮箱", "unverified");
        Sys(tools, "set-otherusers", "其他用户", "Other users", "ms-settings:otherusers", false, "其他用户", "other users", "其他账户", "家庭", "添加用户", "unverified");
        Sys(tools, "set-signinoptions", "登录选项", "Sign-in options", "ms-settings:signinoptions", false, "登录选项", "sign-in options", "密码", "pin", "windows hello", "指纹", "人脸");
        Sys(tools, "set-yourinfo", "你的信息", "Your info", "ms-settings:yourinfo", false, "你的信息", "your info", "账户信息", "头像", "unverified");
        Sys(tools, "set-personalization", "个性化", "Personalization", "ms-settings:personalization", false, "个性化", "personalization", "主题", "外观");
        Sys(tools, "set-personalization-background", "背景", "Background", "ms-settings:personalization-background", false, "背景", "background", "壁纸", "桌面背景", "wallpaper");
        Sys(tools, "set-personalization-colors", "颜色", "Colors", "ms-settings:personalization-colors", false, "颜色", "colors", "主题色", "深色模式", "透明效果");
        Sys(tools, "set-personalization-start", "开始", "Start", "ms-settings:personalization-start", false, "开始", "start", "开始菜单", "固定");
        Sys(tools, "set-taskbar", "任务栏", "Taskbar", "ms-settings:taskbar", false, "任务栏", "taskbar", "托盘", "图标", "合并");
        Sys(tools, "set-lockscreen", "锁屏界面", "Lock screen", "ms-settings:lockscreen", false, "锁屏", "lock screen", "锁屏界面", "锁屏壁纸");
        Sys(tools, "set-themes", "主题", "Themes", "ms-settings:themes", false, "主题", "themes", "主题包", "声音方案");
        Sys(tools, "set-fonts", "字体", "Fonts", "ms-settings:fonts", false, "字体", "fonts", "字体设置", "安装字体");
        Sys(tools, "set-gaming-gamebar", "Xbox Game Bar", "Xbox Game Bar", "ms-settings:gaming-gamebar", false, "game bar", "xbox game bar", "游戏栏", "win+g");
        Sys(tools, "set-gaming-gamedvr", "游戏捕获", "Captures (Game DVR)", "ms-settings:gaming-gamedvr", false, "游戏捕获", "game dvr", "录制", "截图", "后台录制", "unverified");
        Sys(tools, "set-gaming-gamemode", "游戏模式", "Game Mode", "ms-settings:gaming-gamemode", false, "游戏模式", "game mode", "游戏", "性能");
        Sys(tools, "set-dateandtime", "日期和时间", "Date & time", "ms-settings:dateandtime", false, "日期和时间", "date and time", "时区", "自动对时");
        Sys(tools, "set-regionformatting", "区域", "Region", "ms-settings:regionformatting", false, "区域", "region", "区域格式", "日期格式");
        Sys(tools, "set-regionlanguage", "语言", "Language", "ms-settings:regionlanguage", false, "语言", "language", "语言和区域", "输入法", "添加语言");
        Sys(tools, "set-speech", "语音", "Speech", "ms-settings:speech", false, "语音", "speech", "语音语言", "听写");
        Sys(tools, "set-easeofaccess-magnifier", "放大镜（辅助功能）", "Magnifier (Accessibility)", "ms-settings:easeofaccess-magnifier", false, "放大镜", "magnifier", "辅助功能", "轻松使用");
        Sys(tools, "set-easeofaccess-narrator", "讲述人（辅助功能）", "Narrator (Accessibility)", "ms-settings:easeofaccess-narrator", false, "讲述人", "narrator", "朗读", "辅助功能");
        Sys(tools, "set-easeofaccess-highcontrast", "高对比度", "High contrast", "ms-settings:easeofaccess-highcontrast", false, "高对比度", "high contrast", "辅助功能", "unverified");
        Sys(tools, "set-easeofaccess-colorfilter", "颜色滤镜", "Color filters", "ms-settings:easeofaccess-colorfilter", false, "颜色滤镜", "color filters", "色盲", "灰度", "unverified");
        Sys(tools, "set-easeofaccess-closedcaptioning", "隐藏式字幕", "Closed captions", "ms-settings:easeofaccess-closedcaptioning", false, "隐藏式字幕", "closed captions", "字幕", "unverified");
        Sys(tools, "set-easeofaccess-cursor", "文本光标", "Text cursor", "ms-settings:easeofaccess-cursor", false, "文本光标", "text cursor", "光标", "输入指示器", "unverified");
        Sys(tools, "set-easeofaccess-visualeffects", "视觉效果", "Visual effects", "ms-settings:easeofaccess-visualeffects", false, "视觉效果", "visual effects", "动画", "透明度", "unverified");
        Sys(tools, "set-easeofaccess-mousepointer", "鼠标指针和触摸", "Mouse pointer & touch", "ms-settings:easeofaccess-mousepointer", false, "鼠标指针", "mouse pointer", "指针大小", "触摸反馈", "unverified");
        Sys(tools, "set-windowsupdate", "Windows 更新", "Windows Update", "ms-settings:windowsupdate", false, "windows update", "windows更新", "更新", "检查更新");
        Sys(tools, "set-windowsupdate-action", "检查更新", "Check for updates", "ms-settings:windowsupdate-action", false, "检查更新", "check for updates", "立即更新", "unverified");
        Sys(tools, "set-windowsupdate-history", "更新历史记录", "Update history", "ms-settings:windowsupdate-history", false, "更新历史记录", "update history", "已安装更新", "补丁", "unverified");
        Sys(tools, "set-windowsupdate-optionalupdates", "可选更新", "Optional updates", "ms-settings:windowsupdate-optionalupdates", false, "可选更新", "optional updates", "驱动更新", "unverified");
        Sys(tools, "set-windowsupdate-options", "Windows 更新高级选项", "Windows Update advanced options", "ms-settings:windowsupdate-options", false, "更新高级选项", "暂停更新", "更新选项", "unverified");
        Sys(tools, "set-windowsdefender", "Windows 安全中心", "Windows Security", "ms-settings:windowsdefender", false, "windows 安全中心", "windows security", "defender", "安全中心", "病毒防护");
        Sys(tools, "set-privacy", "隐私", "Privacy", "ms-settings:privacy", false, "隐私", "privacy", "隐私和安全", "权限");
        Sys(tools, "set-privacy-location", "位置", "Location", "ms-settings:privacy-location", false, "位置", "location", "定位", "位置权限");
        Sys(tools, "set-privacy-webcam", "相机", "Camera", "ms-settings:privacy-webcam", false, "相机", "camera", "摄像头", "摄像头权限");
        Sys(tools, "set-privacy-microphone", "麦克风", "Microphone", "ms-settings:privacy-microphone", false, "麦克风", "microphone", "麦克风权限", "录音");
        Sys(tools, "set-privacy-activityhistory", "活动历史记录", "Activity history", "ms-settings:privacy-activityhistory", false, "活动历史记录", "activity history", "时间线", "timeline");
        Sys(tools, "set-privacy-speechtyping", "墨迹书写和键入", "Inking & typing", "ms-settings:privacy-speechtyping", false, "墨迹书写", "inking", "键入", "个人词典", "unverified");
        Sys(tools, "set-privacy-contacts", "联系人", "Contacts", "ms-settings:privacy-contacts", false, "联系人", "contacts", "通讯录", "权限", "unverified");
        Sys(tools, "set-privacy-calendar", "日历", "Calendar", "ms-settings:privacy-calendar", false, "日历", "calendar", "权限", "unverified");
        Sys(tools, "set-privacy-email", "电子邮件", "Email", "ms-settings:privacy-email", false, "电子邮件", "email", "邮箱权限", "unverified");
        Sys(tools, "set-privacy-documents", "文档（隐私）", "Documents", "ms-settings:privacy-documents", false, "文档", "documents", "文件权限", "unverified");
        Sys(tools, "set-privacy-pictures", "图片（隐私）", "Pictures", "ms-settings:privacy-pictures", false, "图片", "pictures", "图片权限", "unverified");
        Sys(tools, "set-privacy-videos", "视频（隐私）", "Videos", "ms-settings:privacy-videos", false, "视频", "videos", "视频权限", "unverified");
        Sys(tools, "set-privacy-musiclibrary", "音乐库", "Music library", "ms-settings:privacy-musiclibrary", false, "音乐库", "music library", "音乐权限", "unverified");
        Sys(tools, "set-privacy-feedback", "反馈和诊断", "Feedback & diagnostics", "ms-settings:privacy-feedback", false, "反馈和诊断", "feedback", "诊断数据", "遥测", "unverified");
        Sys(tools, "def-home", "Windows 安全中心（首页）", "Windows Security (home)", "windowsdefender:", false, "windows 安全中心", "windowsdefender", "安全中心", "defender", "unverified");
        Sys(tools, "def-threat", "病毒和威胁防护", "Virus & threat protection", "windowsdefender://threat", false, "病毒和威胁防护", "病毒防护", "threat", "defender扫描", "unverified");
        Sys(tools, "def-account", "账户保护", "Account protection", "windowsdefender://account", false, "账户保护", "account protection", "windows hello", "动态锁", "unverified");
        Sys(tools, "def-network", "防火墙和网络保护", "Firewall & network protection", "windowsdefender://network", false, "防火墙和网络保护", "firewall", "网络保护", "networt", "unverified");
        Sys(tools, "def-appbrowser", "应用和浏览器控制", "App & browser control", "windowsdefender://appbrowser", false, "应用和浏览器控制", "smart screen", "exploit protection", "unverified");
        Sys(tools, "def-devicesecurity", "设备安全性", "Device security", "windowsdefender://devicesecurity", false, "设备安全性", "核心隔离", "tpm", "安全启动", "unverified");
        Sys(tools, "def-perfhealth", "设备性能和运行状况", "Device performance & health", "windowsdefender://perfhealth", false, "设备性能和运行状况", "运行状况报告", "health", "unverified");
        Sys(tools, "def-family", "家庭选项", "Family options", "windowsdefender://family", false, "家庭选项", "family options", "家长控制", "儿童账户", "unverified");
        Sys(tools, "def-history", "保护记录", "Protection history", "windowsdefender://history", false, "保护记录", "protection history", "隔离区", "拦截记录", "unverified");
        Sys(tools, "def-providers", "安全提供商", "Security providers", "windowsdefender://providers", false, "安全提供商", "security providers", "杀毒软件", "防火墙状态", "unverified");
        Folder(tools, "sh-desktop", "桌面", "Desktop", "explorer.exe shell:Desktop", false, "桌面", "desktop", "shell:desktop");
        Folder(tools, "sh-downloads", "下载", "Downloads", "explorer.exe shell:Downloads", false, "下载", "downloads", "shell:downloads");
        Folder(tools, "sh-personal", "文档", "Documents", "explorer.exe shell:Personal", false, "文档", "documents", "我的文档", "personal");
        Folder(tools, "pictures", "图片", "Pictures", "explorer.exe \"shell:My Pictures\"", false, "图片", "pictures", "我的图片", "相册");
        Folder(tools, "music", "音乐", "Music", "explorer.exe \"shell:My Music\"", false, "音乐", "music", "我的音乐");
        Folder(tools, "videos", "视频", "Videos", "explorer.exe \"shell:My Video\"", false, "视频", "videos", "我的视频", "movie");
        Folder(tools, "sh-recyclebinfolder", "回收站", "Recycle Bin", "explorer.exe shell:RecycleBinFolder", false, "回收站", "recycle bin", "recyclebinfolder", "删除");
        Folder(tools, "sh-mycomputerfolder", "此电脑", "This PC", "explorer.exe shell:MyComputerFolder", false, "此电脑", "this pc", "我的电脑", "计算机", "mycomputerfolder");
        Folder(tools, "sh-networkplacesfolder", "网络", "Network", "explorer.exe shell:NetworkPlacesFolder", false, "网络", "network", "网上邻居", "networkplacesfolder");
        Folder(tools, "sh-profile", "用户主目录", "User Profile Folder", "explorer.exe shell:Profile", false, "用户主目录", "profile", "用户文件夹", "home");
        Folder(tools, "sh-appdata", "应用程序数据（Roaming）", "AppData (Roaming)", "explorer.exe shell:AppData", false, "appdata", "roaming", "应用程序数据", "配置文件");
        Folder(tools, "local-appdata", "本地应用程序数据", "Local AppData", "explorer.exe \"shell:Local AppData\"", false, "localappdata", "local appdata", "appdata", "本地缓存");
        Folder(tools, "sh-programs", "开始菜单程序", "Start Menu Programs", "explorer.exe shell:Programs", false, "开始菜单程序", "programs", "开始菜单", "快捷方式");
        Folder(tools, "start-menu", "开始菜单", "Start Menu", "explorer.exe \"shell:Start Menu\"", false, "开始菜单", "start menu", "开始");
        Folder(tools, "sh-startup", "启动", "Startup", "explorer.exe shell:Startup", false, "启动", "startup", "开机启动", "启动文件夹");
        Folder(tools, "common-startup", "公共启动", "Common Startup", "explorer.exe \"shell:Common Startup\"", false, "公共启动", "common startup", "所有用户启动", "unverified");
        Folder(tools, "sh-sendto", "发送到", "SendTo", "explorer.exe shell:SendTo", false, "发送到", "sendto", "右键发送");
        Folder(tools, "sh-recent", "最近使用的项目", "Recent Items", "explorer.exe shell:Recent", false, "最近使用的项目", "recent", "最近文件", "最近访问");
        Folder(tools, "sh-fonts", "字体文件夹", "Fonts", "explorer.exe shell:Fonts", false, "字体", "fonts", "字体文件夹");
        Folder(tools, "sh-libraries", "库", "Libraries", "explorer.exe shell:Libraries", false, "库", "libraries", "文档库", "图片库");
        Folder(tools, "sh-onedrive", "OneDrive", "OneDrive", "explorer.exe shell:OneDrive", false, "onedrive", "云盘", "网盘", "unverified");
        Folder(tools, "sh-public", "公用文件夹", "Public Folder", "explorer.exe shell:Public", false, "公用文件夹", "public", "共享文件", "unverified");
        Folder(tools, "sh-appsfolder", "应用程序", "Applications", "explorer.exe shell:AppsFolder", false, "应用程序", "appsfolder", "所有应用", "应用列表", "unverified");
        Folder(tools, "sh-screenshots", "屏幕截图", "Screenshots", "explorer.exe shell:Screenshots", false, "屏幕截图", "screenshots", "截图文件夹", "unverified");
        Folder(tools, "sh-favorites", "收藏夹", "Favorites", "explorer.exe shell:Favorites", false, "收藏夹", "favorites", "ie收藏夹", "unverified");
        Folder(tools, "quick-launch", "快速启动", "Quick Launch", "explorer.exe \"shell:Quick Launch\"", false, "快速启动", "quick launch", "任务栏快速启动", "unverified");
        Folder(tools, "sh-windows", "Windows 目录", "Windows Directory", "explorer.exe shell:Windows", false, "windows目录", "windows", "系统目录", "winnt", "unverified");
        Folder(tools, "sh-system", "System32 目录", "System32 Directory", "explorer.exe shell:System", false, "system32", "system", "系统文件夹", "unverified");
        Folder(tools, "sh-programfiles", "Program Files", "Program Files", "explorer.exe shell:ProgramFiles", false, "program files", "程序目录", "安装目录", "unverified");
        Folder(tools, "sh-programfilesx86", "Program Files (x86)", "Program Files (x86)", "explorer.exe shell:ProgramFilesX86", false, "program files x86", "32位程序", "unverified");
        Folder(tools, "sh-searches", "搜索文件夹", "Searches", "explorer.exe shell:Searches", false, "搜索", "searches", "保存的搜索", "unverified");
        Tool(tools, "pwsh", "PowerShell 7", "PowerShell 7", "pwsh", false, "pwsh", "powershell7");
        Tool(tools, "mdsched", "Windows 内存诊断", "Windows Memory Diagnostic", "mdsched", true, "内存诊断", "内存检测", "mdsched");
        Tool(tools, "sigverif", "文件签名验证工具", "File Signature Verification", "sigverif", false, "签名验证", "sigverif");
        Tool(tools, "shrpubw", "创建共享文件夹向导", "Create Shared Folder Wizard", "shrpubw", false, "共享向导", "shrpubw");
        Tool(tools, "iexpress", "IExpress 向导", "IExpress Wizard", "iexpress", false, "自解压", "打包", "iexpress");
        Tool(tools, "verifier", "驱动程序验证程序管理器", "Driver Verifier Manager", "verifier", true, "驱动验证", "verifier");
        Tool(tools, "wfs", "Windows 传真和扫描", "Windows Fax and Scan", "WFS", false, "传真", "扫描", "wfs");
        Tool(tools, "devicepairingwizard", "添加设备", "Add a device", "devicepairingwizard", false, "添加设备", "设备配对", "devicepairingwizard");
        Tool(tools, "sh-appsfoldermicrosoftwindowssoundrecorder8wekyb3d8bbweapp", "录音机", "Sound Recorder", "explorer.exe shell:AppsFolder\\Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe!App", false, "录音", "录音机", "soundrecorder");
        Tool(tools, "isoburn", "Windows 光盘映像刻录机", "Windows Disc Image Burner", "isoburn", false, "iso", "刻录", "isoburn");
        Tool(tools, "sndvol", "音量合成器（经典）", "Volume Mixer (Classic)", "sndvol", false, "单独音量", "混音器", "sndvol");
        Tool(tools, "odbcad32", "ODBC 数据源(64 位)", "ODBC Data Sources (64-bit)", "odbcad32", true, "odbc", "数据源", "数据库", "odbcad32");

        return tools;
    }

    /// <summary>系统工具条目</summary>
    private static void Tool(List<SystemTool> list, string id, string zh, string en, string launch, bool admin, params string[] kw)
        => Add(list, id, zh, en, launch, SystemToolCategory.Tool, admin, kw);

    /// <summary>控制面板项条目</summary>
    private static void Cpl(List<SystemTool> list, string id, string zh, string en, string launch, bool admin, params string[] kw)
        => Add(list, id, zh, en, launch, SystemToolCategory.ControlPanel, admin, kw);

    /// <summary>MMC 管理控制台条目</summary>
    private static void Msc(List<SystemTool> list, string id, string zh, string en, string launch, bool admin, params string[] kw)
        => Add(list, id, zh, en, launch, SystemToolCategory.MscConsole, admin, kw);

    /// <summary>Windows 设置页 / 安全中心深链条目</summary>
    private static void Sys(List<SystemTool> list, string id, string zh, string en, string launch, bool admin, params string[] kw)
        => Add(list, id, zh, en, launch, SystemToolCategory.Settings, admin, kw);

    /// <summary>系统特殊文件夹条目</summary>
    private static void Folder(List<SystemTool> list, string id, string zh, string en, string launch, bool admin, params string[] kw)
        => Add(list, id, zh, en, launch, SystemToolCategory.ShellFolder, admin, kw);

    private static void Add(List<SystemTool> list, string id, string zh, string en, string launch,
        SystemToolCategory category, bool admin, string[] kw)
    {
        list.Add(new SystemTool
        {
            Id = id,
            Name = zh,
            NameEn = en,
            Launch = launch,
            Category = category,
            RequiresAdmin = admin,
            Keywords = new List<string>(kw)
        });
    }
}
