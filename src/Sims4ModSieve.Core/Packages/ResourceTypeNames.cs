namespace Sims4ModSieve.Core.Packages;

public static class ResourceTypeNames
{
    public static string DisplayName(uint type, string? englishName, bool isXmlTuning)
    {
        if (englishName is null) return $"0x{type:X8}";

        var chineseName = type switch
        {
            0x8B18FF6E => "捏人滑杆热点",
            0x00B2D882 or 0x2BC04EDF or 0x3453CF95 or 0xBA856C78 or 0x2F7D0004 or 0x2F7D0006 => "贴图",
            0x034AEECB => "捏人部件",
            0x220557DA => "文本表",
            0x545AC67A => "模拟数据",
            0x0000000F or 0x00000010 or 0x00000012 or 0x00000014 or 0x00000015 or 0x0D338A3A
                or 0x16CA6BC4 or 0x3BD45407 or 0x3C1AF1F2 or 0x3C2A8647 or 0x5B282D45 or 0x8E71065D
                or 0x9C925813 or 0xA1FF2FC4 or 0xAB19BCBA or 0xB0118C15 or 0xB67673A2 or 0xBD491726
                or 0xCD9DE247 => "缩略图",
            0x00000013 => "缩略图遮罩",
            0xB93A9915 => "缩略图缓存",
            0x54BD4618 => "自动化调参",
            0x62E94D38 => "合并调参",
            0xC0DB5AE7 => "物件定义",
            0x319E4F1D => "物件目录",
            0x015A1849 => "模型",
            0x8EAF13DE => "骨骼",
            0x067CAA11 => "混合模型",
            0xC5F6763E => "捏人滑杆调整",
            0xD382BF57 or 0xB734E44F => "占地",
            0x03B4C61D => "光照",
            _ when isXmlTuning => "调参 XML",
            _ => null,
        };

        return chineseName is null ? $"{englishName}（0x{type:X8}）" : $"{chineseName}（{englishName}）";
    }
}
