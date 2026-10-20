namespace QuickPalette.Domain.Services;

using QuickPalette.Domain.Models;

/// <summary>
/// 拼音提供器：汉字 → 全拼 / 首字母（离线内置码表，无第三方依赖）
/// </summary>
public interface IPinyinProvider
{
    /// <summary>全拼（多音字取常用读音；无汉字原样小写返回）</summary>
    string GetFullPinyin(string text);

    /// <summary>首字母（每字取声母首字母；无汉字原样小写返回）</summary>
    string GetInitials(string text);
}

/// <summary>
/// 内置码表拼音提供器。
/// 码表覆盖约 500 常用汉字（含多音字备选读音，如 长 chang|zhang、重 zhong|chong）；
/// 生僻字不在表中时按原字符保留，可用别名兜底。
/// 多音字会生成多个候选拼法（上限 8 个），查询命中其一即可。
/// </summary>
public sealed class PinyinProvider : IPinyinProvider
{
    public static PinyinProvider Instance { get; } = new();

    /// <summary>单个名字生成的拼音候选数量上限（防多音字组合爆炸）</summary>
    private const int MaxVariants = 8;

    private PinyinProvider()
    {
    }

    /// <summary>
    /// 补全 AppItem 的拼音字段（Name + DisplayName；已有值不覆盖）
    /// </summary>
    public void EnsurePinyin(AppItem? item)
    {
        if (item is null) return;
        var source = $"{item.Name} {item.DisplayName}".Trim();
        if (string.IsNullOrEmpty(source)) return;

        if (string.IsNullOrEmpty(item.Pinyin))
            item.Pinyin = string.Join(' ', FullPinyinVariants(source, 4));
        if (string.IsNullOrEmpty(item.PinyinInitials))
            item.PinyinInitials = string.Join(' ', InitialVariants(source, 4));
    }

    public string GetFullPinyin(string text)
    {
        var first = FullPinyinVariants(text, 1);
        return first.Count > 0 ? first[0] : string.Empty;
    }

    public string GetInitials(string text)
    {
        var first = InitialVariants(text, 1);
        return first.Count > 0 ? first[0] : string.Empty;
    }

    /// <summary>全拼候选（首元素为常用读音拼法）</summary>
    public List<string> FullPinyinVariants(string? text, int cap = MaxVariants)
        => BuildVariants(text, fullPinyin: true, cap);

    /// <summary>首字母候选（首元素为常用读音拼法）</summary>
    public List<string> InitialVariants(string? text, int cap = MaxVariants)
        => BuildVariants(text, fullPinyin: false, cap);

    private static List<string> BuildVariants(string? text, bool fullPinyin, int cap)
    {
        var variants = new List<string> { string.Empty };
        if (string.IsNullOrEmpty(text)) return variants;

        foreach (var ch in text)
        {
            if (ch == ' ' || ch == '\u3000')
            {
                for (var i = 0; i < variants.Count; i++)
                {
                    if (variants[i].Length > 0 && !variants[i].EndsWith(' '))
                        variants[i] += ' ';
                }
                continue;
            }

            var readings = GetReadings(ch);
            if (readings is null)
            {
                // 非汉字/表外字：原样保留（小写），保证 ASCII 名称拼音可搜
                var literal = char.ToLowerInvariant(ch).ToString();
                for (var i = 0; i < variants.Count; i++) variants[i] += literal;
                continue;
            }

            var extra = new List<string>();
            foreach (var reading in readings)
            {
                var piece = fullPinyin ? reading : reading[..1];
                for (var i = 0; i < variants.Count; i++)
                {
                    if (extra.Count + variants.Count > cap * 2) break;
                    extra.Add(variants[i] + piece);
                }
            }
            variants = extra.Count > 0 ? extra : variants;
            if (variants.Count > cap) variants.RemoveRange(cap, variants.Count - cap);
        }

        return variants;
    }

    /// <summary>取汉字读音（首元素为常用读音）；表外返回 null</summary>
    private static string[]? GetReadings(char ch)
    {
        if (Table.TryGetValue(ch, out var readings)) return readings;

        // 兼容补充：全角 ASCII（Ａ→a 等）不在码表，按半角小写处理
        return null;
    }

    private static readonly Dictionary<char, string[]> Table = BuildTable();

    private static Dictionary<char, string[]> BuildTable()
    {
        var map = new Dictionary<char, string[]>(1024);
        foreach (var entry in TableData.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = entry.IndexOf(':');
            if (idx <= 0 || idx + 1 >= entry.Length) continue;
            map[entry[0]] = entry[(idx + 1)..].Split('|');
        }
        return map;
    }

    // 码表：「字:常用读音|备选读音;」紧凑存储，首次使用时解析成字典
    private const string TableData =
        "一:yi;二:er;三:san;四:si;五:wu;六:liu;七:qi;八:ba;九:jiu;十:shi;百:bai;千:qian;万:wan;亿:yi;" +
        "年:nian;月:yue;日:ri;时:shi;分:fen;秒:miao;天:tian;号:hao;期:qi;" +
        "上:shang;下:xia;左:zuo;右:you;前:qian;后:hou;里:li;外:wai;中:zhong;内:nei;" +
        "大:da;小:xiao;多:duo;少:shao;高:gao;低:di;长:chang|zhang;短:duan;宽:kuan;厚:hou;深:shen;浅:qian;" +
        "人:ren;男:nan;女:nv;孩:hai;子:zi;父:fu;母:mu;哥:ge;姐:jie;弟:di;妹:mei;朋:peng;友:you;们:men;" +
        "我:wo;你:ni;他:ta;她:ta;它:ta;的:de|di;了:le|liao;在:zai;有:you;和:he|hu|huo;与:yu;及:ji;或:huo;" +
        "是:shi;不:bu;也:ye;都:du|dou;就:jiu;还:hai|huan;才:cai;又:you;再:zai;很:hen;太:tai;最:zui;更:geng;" +
        "比:bi;较:jiao;会:hui|kuai;能:neng;可:ke;以:yi;要:yao;应:ying;该:gai;需:xu;想:xiang;" +
        "意:yi;思:si;觉:jue;得:de|dei;知:zhi;道:dao;识:shi|zhi;懂:dong;爱:ai;喜:xi;欢:huan;" +
        "说:shuo;话:hua;语:yu;言:yan;文:wen;字:zi;读:du;写:xie;看:kan;听:ting;见:jian;问:wen;答:da;" +
        "来:lai;去:qu;到:dao;从:cong;往:wang;过:guo;回:hui;出:chu;进:jin;入:ru;走:zou;跑:pao;飞:fei;" +
        "坐:zuo;站:zhan;开:kai;关:guan;启:qi;停:ting;动:dong;退:tui;唤:huan;醒:xing;驻:zhu;" +
        "工:gong;作:zuo;事:shi;情:qing;务:wu;项:xiang;目:mu;任:ren;课:ke;程:cheng;序:xu;件:jian;" +
        "软:ruan;硬:ying;电:dian;脑:nao;机:ji;手:shou;页:ye;面:mian;窗:chuang;口:kou;菜:cai;单:dan|shan;" +
        "栏:lan;图:tu;标:biao;按:an;钮:niu;键:jian;盘:pan;鼠:shu;幕:mu;显:xian;示:shi;器:qi;卡:ka|qia;" +
        "存:cun;盘:pan;驱:qu;容:rong;磁:ci;频:pin;率:lv|shuai;速:su;度:du;级:ji;别:bie;等:deng;" +
        "搜:sou;索:suo;查:cha|zha;找:zhao;结:jie;果:guo;列:lie;表:biao;排:pai;类:lei;组:zu;筛:shai;" +
        "选:xuan;择:ze;记:ji;录:lu;历:li;史:shi;收:shou;藏:cang;推:tui;荐:jian;隐:yin;" +
        "设:she;置:zhi;配:pei;管:guan;理:li;员:yuan;权:quan;限:xian;安:an;全:quan;私:si;保:bao;护:hu;" +
        "备:bei;份:fen;恢:hui;复:fu;导:dao;出:chu;删:shan;增:zeng;改:gai;修:xiu;重:zhong|chong;卸:xie;载:zai;装:zhuang;" +
        "热:re;快:kuai;捷:jie;方:fang;式:shi;托:tuo;常:chang;前:qian;后:hou;台:tai;化:hua;皮:pi;肤:fu;" +
        "主:zhu;题:ti;色:se|shai;黑:hei;白:bai;红:hong;蓝:lan;绿:lv;黄:huang;紫:zi;灰:hui;亮:liang;" +
        "命:ming;令:ling;模:mo|mu;块:kuai;插:cha;扩:kuo;展:zhan;架:jia;构:gou;层:ceng;接:jie;协:xie;议:yi;" +
        "网:wang;络:luo;服:fu;端:duan;流:liu;览:lan;连:lian;断:duan;线:xian;无:wu;有:you;" +
        "数:shu|shuo;量:liang;距:ju;离:li;位:wei;根:gen;路:lu;径:jing;卷:juan;夹:jia;带:dai;包:bao;" +
        "新:xin;旧:jiu;老:lao;早:zao;晚:wan;现:xian;当:dang;将:jiang;已:yi;未:wei;曾:ceng|zeng;经:jing;正:zheng;刚:gang;" +
        "为:wei;行:xing|hang;真:zhen;假:jia;对:dui;错:cuo;好:hao;坏:huai;慢:man;简:jian;杂:za;易:yi;难:nan;容:rong;" +
        "国:guo;家:jia;城:cheng;市:shi;区:qu|ou;县:xian;街:jie;门:men;楼:lou;室:shi;间:jian;房:fang;屋:wu;" +
        "气:qi;雨:yu;雪:xue;风:feng;云:yun;星:xing;光:guang;灯:deng;明:ming;暗:an;阴:yin;阳:yang;" +
        "山:shan;水:shui;河:he;湖:hu;海:hai;土:tu;石:shi|dan? ;木:mu;草:cao;花:hua;树:shu;叶:ye|xie;" +
        "生:sheng;活:huo;学:xue;校:xiao;师:shi;医:yi;院:yuan;药:yao;病:bing;" +
        "车:che;船:chuan;桥:qiao;铁:tie;公:gong;交:jiao;路:lu;站:zhan;地:di|de;" +
        "金:jin;钱:qian;元:yuan;价:jia;费:fei;付:fu;票:piao;账:zhang;银:yin;行:xing|hang;商:shang;业:ye;" +
        "消:xiao;息:xi;信:xin;邮:you;箱:xiang;微:wei;博:bo;群:qun;聊:liao;音:yin;视:shi;片:pian;相:xiang;照:zhao;" +
        "游:you;戏:xi;乐:le|yue;歌:ge;曲:qu;影:ying;剧:ju;" +
        "球:qiu;运:yun;体:ti;育:yu;跑:pao;跳:tiao;" +
        "北:bei;东:dong;南:nan;西:xi;" +
        "吃:chi;饭:fan;茶:cha;酒:jiu;肉:rou;米:mi;面:mian;包:bao;衣:yi;服:fu;鞋:xie;帽:mao;" +
        "报:bao;纸:zhi;书:shu;笔:bi;本:ben;具:ju;刀:dao;剪:jian;锁:suo;钥:yao;匙:shi;" +
        "支:zhi;宝:bao;淘:tao;京:jing;抖:dou;豆:dou;瓣:ban;钉:ding;墨:mo;兴:xing|gong;建:jian;移:yi;腾:teng;讯:xun;" +
        "德:de;华:hua;为:wei;联:lian;想:xiang;中:zhong;兴:xing|gong;米:mi;蓝:lan;科:ke;技:ji;创:chuang;新:xin;" +
        "品:pin;牌:pai;版:ban;精:jing;简:jian;专:zhuan;业:ye;家:jia;个:ge;人:ren;公:gong;司:si;" +
        "阿:a;啊:a;啦:la;吧:ba;呀:ya;哦:o;噢:o;嘿:hei;嗨:hai;" +
        "北:bei;斗:dou;星:xing;座:zuo;辰:chen;" +
        "笔:bi;记:ji;本:ben;设:she;备:bei;控:kong;制:zhi;中:zhong;心:xin;中:zhong;央:yang;" +
        "输:shu;法:fa;切:qie;换:huan;拼:pin;音:yin;庆:qing;表:biao;州:zhou;台:tai;湾:wan;" +
        "哔:bi;哩:li;百:bai;度:du;迅:xun;雷:lei;酷:ku;狗:gou;云:yun;盘:pan;网:wang;易:yi;严:yan;选:xuan;" +
        "设:she;置:zhi;默:mo;认:ren;语:yu;言:yan;繁:fan;体:ti;" +
        "另:ling;附:fu;近:jin;远:yuan;邻:lin;旁:pang;边:bian;角:jiao|jue;顶:ding;底:di;部:bu;总:zong;" +
        "第:di;次:ci;遍:bian;趟:tang;轮:lun;场:chang;阵:zhen;" +
        "件:jian;套:tao;张:zhang;条:tiao;只:zhi;头:tou;辆:liang;座:zuo;" +
        "态:tai;状:zhuang;况:kuang;果:guo;然:ran;而:er;且:qie;但:dan;却:que;因:yin;所:suo;此:ci;" +
        "乎:hu;者:zhe;之:zhi;乎:hu;者:zhe;焉:yan;哉:zai;" +
        "示:shi;例:li;编:bian;辑:ji;器:qi;视:shi;图:tu;打:da;印:yin;机:ji;扫:sao;描:miao;仪:yi;" +
        "万:wan;能:neng;助:zhu;手:shou;小:xiao;巧:qiao;轻:qing;便:bian;强:qiang;大:da;功:gong;能:neng;" +
        "特:te;色:se|shai;亮:liang;点:dian;优:you;缺:que;支:zhi;持:chi;兼:jian;容:rong;稳:wen;定:ding;" +
        "感:gan;觉:jue;体:ti;验:yan;交:jiao;互:hu;流:liu;畅:chang;顺:shun;滑:hua;卡:ka|qia;顿:dun;" +
        "异:yi;常:chang;错:cuo;误:wu;警:jing;告:gao;提:ti;示:shi;信:xin;息:xi;日:ri;志:zhi;报:bao;表:biao;" +
        "升:sheng;级:ji;更:geng;新:xin;版:ban;本:ben;补:bu;丁:ding;发:fa;布:bu;部:bu;署:shu;配:pei;置:zhi;" +
        "独:du;立:li;免:mian;费:fei;开:kai;源:yuan;开:kai;放:fang;闭:bi;源:yuan;商:shang;用:yong;" +
        "只:zhi;读:du;写:xie;权:quan;限:xian;访:fang;问:wen;拒:ju;绝:jue;允:yun;许:xu;" +
        "身:shen;份:fen;证:zheng;账:zhang;号:hao;密:mi;码:ma;登:deng;录:lu;注:zhu;销:xiao;" +
        "缓:huan;存:cun;队:dui;列:lie;栈:zhan;堆:dui;表:biao;树:shu;图:tu;链:lian;表:biao;集:ji;合:he;" +
        "顺:shun;序:xu;倒:dao;随:sui;即:ji;刻:ke;瞬:shun;间:jian;持:chi;续:xu;" +
        "空:kong;闲:xian;忙:mang;繁:fan;重:zhong|chong;轻:qing;松:song;紧:jin;急:ji;缓:huan;慢:man;" +
        "整:zheng;齐:qi;乱:luan;洁:jie;净:jing;脏:zang;干:gan;燥:zao;湿:shi;冷:leng;热:re;暖:nuan;" +
        "真:zhen;实:shi;虚:xu;拟:ni;幻:huan;想:xiang;创:chuang;意:yi;灵:ling;感:gan;思:si;维:wei;" +
        "单:dan|shan;位:wei;单:dan;元:yuan;模:mo|mu;块:kuai;组:zu;件:jian;部:bu;件:jian;总:zong;线:xian;" +
        "进:jin;程:cheng;线:xian;程:cheng;并:bing;发:fa;串:chuan;行:xing|hang;异:yi;步:bu;同:tong;步:bu;" +
        "轮:lun;询:xun;事:shi;件:jian;消:xiao;息:xi;队:dui;列:lie;订:ding;阅:yue;发:fa;布:bu;广:guang;播:bo;" +
        "输:shu;入:ru;输:shu;出:chu;缓:huan;冲:chong;流:liu;管:guan;道:dao;通:dao;知:zhi;回:hui;调:diao;" +
        "句:ju;法:fa;词:ci;语:yu;义:yi;编:bian;译:yi;解:jie|xie;释:shi;执:zhi;行:xing|hang;调:tiao|diao;试:shi;测:shi;验:yan;" +
        "版:ban;本:ben;控:kong;制:zhi;仓:cang;库:ku;分:fen;支:zhi;合:he;并:bing;冲:chong;突:tu;决:jue;" +
        "科:ke;学:xue;研:yan;究:jiu;实:shi;验:yan;室:shi;图:tu;书:shu;馆:guan;博:bo;物:wu;馆:guan;" +
        "行:xing|hang;政:zheng;府:fu;部:bu;门:men;机:ji;构:gou;组:zu;织:zhi;团:tuan;队:dui;协:xie;会:hui;" +
        "通:tong;讯:xun;联:lian;络:luo;电:dian;话:hua;短:duan;信:xin;微:wei;信:xin;群:qun;聊:liao;直:zhi;播:bo;" +
        "购:gou;物:wu;商:shang;店:dian;超:chao;市:shi;商:shang;城:cheng;订:ding;单:dan;物:wu;流:liu;快:kuai;递:di;" +
        "健:jian;康:kang;运:yun;动:dong;健:jian;身:shen;跑:pao;步:bu;游:you;泳:yong;骑:qi;车:che;" +
        "旅:lv;游:you;酒:jiu;店:dian;机:ji;场:chang|chang? ;火:huo;车:che;地:di|de;铁:tie;公:gong;交:jiao;出:chu;租:zu;车:che;" +
        "教:jiao;育:yu;培:pei;训:xun;考:kao;试:shi;成:cheng;绩:ji;毕:bi;业:ye;学:xue;位:wei;论:lun;文:wen;" +
        "银:yin;行:xing|hang;支:zhi;付:fu;转:zhuan;账:zhang;汇:hui;款:kuan;贷:dai;利:li;率:lv|shuai;投:tou;资:zi;股:gu;票:piao;" +
        "医:yi;疗:liao;健:jian;康:kang;药:yao;品:pin;诊:zhen;所:suo;住:zhu;院:yuan;挂:gua;号:hao;体:ti;检:jian;" +
        "美:mei;食:shi;餐:can;厅:ting;外:wai;卖:mai;点:dian;餐:can;饮:yin;料:liao;咖:ka;啡:fei;奶:nai;茶:cha;" +
        "音:yin;乐:le|yue;播:bo;放:fang;器:qi;视:shi;频:pin;播:bo;放:fang;器:qi;图:tu;片:pian;查:cha|zha;看:kan;器:qi;相:xiang;册:ce;" +
        "邮:you;件:jian;收:shou;件:jian;箱:xiang;发:fa;件:jian;箱:xiang;附:fu;件:jian;地:di|de;址:zhi;联:lian;系:xi;人:ren;" +
        "温:wen;度:du;湿:shi;度:du;气:qi;压:ya;风:feng;速:su;天:tian;气:qi;预:yu;报:bao;气:qi;候:hou;" +
        "动:dong;物:wu;植:zhi;物:wu;猫:mao;狗:gou;鸟:niao;鱼:yu;马:ma;牛:niu;羊:yang;猪:zhu;" +
        "爸:ba;爷:ye;奶:nai;叔:shu;伯:bo;姑:gu;舅:jiu;孙:sun;侄:zhi;亲:qin;戚:qi;" +
        "春:chun;夏:xia;秋:qiu;冬:dong;季:ji;节:jie;假:jia;期:qi;暑:shu;寒:han;" +
        "生:sheng;婚:hun;礼:li;丧:sang;葬:zang;庆:qing;典:dian;派:pai;对:dui;" +
        "衣:yi;裤:ku;裙:qun;袜:wa;帽:mao;围:wei;巾:jin;手:shou;套:tao;眼:yan;镜:jing;" +
        "桌:zhuo;椅:yi;柜:gui;床:chuang;沙:sha;发:fa;灯:deng;泡:pao;插:cha;座:zuo;开:kai;关:guan;" +
        "洗:xi;衣:yi;机:ji;冰:bing;箱:xiang;空:kong;调:tiao|diao? ;电:dian;视:shi;音:xiang;箱:xiang;风:feng;扇:shan;" +
        "牙:ya;刷:shua;毛:mao;巾:jin;肥:fei;皂:zao;洗:xi;发:fa;水:shui;沐:mu;浴:yu;露:lu;" +
        "零:ling;食:shi;糖:tang;果:guo;饼:bing;干:gan;巧:qiao;克:ke;力:li;牛:niu;奶:nai;酸:suan;奶:nai;" +
        "苹:ping;果:guo;香:xiang;蕉:jiao;橘:ju;子:zi;葡:pu;萄:tao;西:xi;瓜:gua;草:cao;莓:mei;芒:mang;果:guo;" +
        "白:bai;菜:cai;萝:luo;卜:bo;土:tu;豆:dou;西:xi;红:hong;柿:shi;黄:huang;瓜:gua;茄:qie;子:zi;" +
        "米:mi;饭:fan;面:mian;条:tiao;饺:jiao;子:zi;包:bao;子:zi;馒:man;头:tou;粥:zhou;汤:tang;";
}
