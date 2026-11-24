using System.Reflection;
using BazaarCore.Tests.TestInfrastructure;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 4C（任务 #11）：PvP VS 名牌中文展示对齐强断言单测。
/// 通过 Wave2CLauncherAssemblyFixture 加载真实 BazaarLauncher.dll 中的零 Unity 顶层格式化器
/// BazaarLauncher.Modules.PvpNameplateFormatter，验证：
///   1. 彻底清除 (Normal) / (Ranked) / (Strong) / (MetaBoss) 等模式与强度桶噪声后缀；
///   2. 英雄英文名、英文占位名与英文段位/头衔 100% 映射为统一清爽中文；
///   3. 锦标赛真实玩家昵称、自定义战队标签与隐私屏蔽符 "******" 零误伤；
///   4. 英文界面态（isChineseLocale=false）100% 保持原值无损；
///   5. LocalizationOverlayModule / TournamentLocalizationPatch 正确挂载 HeroBannerController 与 GetOpponentDisplayName。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave4C_PvpNameplateLocalizationTests : IClassFixture<Wave2CLauncherAssemblyFixture>
{
    private readonly Wave2CLauncherAssemblyFixture _fx;

    public Wave4C_PvpNameplateLocalizationTests(Wave2CLauncherAssemblyFixture fx)
    {
        _fx = fx;
    }

    private Type GetFormatterType()
        => _fx.LauncherAssembly.GetType("BazaarLauncher.Modules.PvpNameplateFormatter", throwOnError: true)!;

    private string InvokeFormatOpponentName(string? rawName, bool isChineseLocale, bool isTournament = false)
    {
        Type t = GetFormatterType();
        MethodInfo m = t.GetMethod(
            "FormatOpponentName",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(string), typeof(bool), typeof(bool) },
            null)!;
        Assert.NotNull(m);
        return (string)m.Invoke(null, new object?[] { rawName, isChineseLocale, isTournament })!;
    }

    private string InvokeFormatTitleOrRankLabel(string? rawLabel, bool isChineseLocale)
    {
        Type t = GetFormatterType();
        MethodInfo m = t.GetMethod(
            "FormatTitleOrRankLabel",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(string), typeof(bool) },
            null)!;
        Assert.NotNull(m);
        return (string)m.Invoke(null, new object?[] { rawLabel, isChineseLocale })!;
    }

    [Theory]
    [InlineData("Vanessa (Normal)", "瓦内莎")]
    [InlineData("Vanessa (Ranked)", "瓦内莎")]
    [InlineData("Vanessa (Unranked)", "瓦内莎")]
    [InlineData("Vanessa (Casual)", "瓦内莎")]
    [InlineData("Pygmalien (Strong)", "皮格马利翁")]
    [InlineData("Dooley (MetaBoss)", "杜利")]
    [InlineData("Mak (Weak)", "马克")]
    [InlineData("Stelle (Normal)", "斯黛拉")]
    [InlineData("Jules (Normal)", "朱尔斯")]
    [InlineData("Karnok (Normal)", "卡诺克")]
    [InlineData("The Dragons (Normal)", "双龙")]
    [InlineData("Hero8 (Normal)", "双龙")]
    [InlineData("Vanessa（Normal）", "瓦内莎")]
    [InlineData("Vanessa (Gold) (Normal)", "瓦内莎")]
    [InlineData("Bronze Vanessa (Normal)", "瓦内莎")]
    [InlineData("Vanessa - Diamond (Ranked)", "瓦内莎")]
    [InlineData("Vanessa", "瓦内莎")]
    [InlineData("Challenger", "挑战者")]
    [InlineData("Small Boss", "挑战者")]
    [InlineData("Unknown Player", "未知玩家")]
    [InlineData("Opponent assigned on selection", "选择后分配对手")]
    [InlineData("Vanessa镜像(Normal)", "瓦内莎镜像")]
    public void FormatOpponentName_ChineseLocale_StripsNoiseAndLocalizesHeroOrPlaceholder(string raw, string expected)
    {
        string actual = InvokeFormatOpponentName(raw, isChineseLocale: true, isTournament: false);
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("(Normal)", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(Ranked)", actual, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Alice", "Alice")]
    [InlineData("张三", "张三")]
    [InlineData("Kripp", "Kripp")]
    [InlineData("Alice (TSM)", "Alice (TSM)")]
    [InlineData("Bob (CN)", "Bob (CN)")]
    [InlineData("[AI] 选手1", "[AI] 选手1")]
    [InlineData("Alice (Normal)", "Alice")]
    [InlineData("张三 (Ranked)", "张三")]
    [InlineData("******", "******")]
    public void FormatOpponentName_PreservesTournamentAndRealPlayerNamesAndPrivacyMask(string raw, string expected)
    {
        string actualTourney = InvokeFormatOpponentName(raw, isChineseLocale: true, isTournament: true);
        Assert.Equal(expected, actualTourney);

        string actualStandard = InvokeFormatOpponentName(raw, isChineseLocale: true, isTournament: false);
        Assert.Equal(expected, actualStandard);
    }

    [Fact]
    public void FormatOpponentName_TournamentVsStandard_DistinguishesRealPlayerNamedWithTeamTag()
    {
        // 锦标赛玩家带战队标签的姓名绝不被当作纯英雄名改写
        Assert.Equal("Vanessa (TSM)", InvokeFormatOpponentName("Vanessa (TSM)", isChineseLocale: true, isTournament: true));
        // 锦标赛真实玩家名若无噪音，保持原样；机器人名若为纯 "Vanessa (Normal)" 依然干净汉化
        Assert.Equal("DragonSlayer", InvokeFormatOpponentName("DragonSlayer", isChineseLocale: true, isTournament: true));
        Assert.Equal("瓦内莎", InvokeFormatOpponentName("Vanessa (Normal)", isChineseLocale: true, isTournament: true));
    }

    [Theory]
    [InlineData("Vanessa (Normal)")]
    [InlineData("Bronze")]
    [InlineData("Alice (Ranked)")]
    [InlineData("******")]
    public void FormatOpponentName_EnglishLocale_ReturnsExactOriginalText(string raw)
    {
        string actual = InvokeFormatOpponentName(raw, isChineseLocale: false, isTournament: false);
        Assert.Equal(raw, actual);
    }

    [Theory]
    [InlineData("Bronze", "青铜")]
    [InlineData("Silver", "白银")]
    [InlineData("Gold", "黄金")]
    [InlineData("Diamond", "钻石")]
    [InlineData("Legend", "传奇")]
    [InlineData("Legendary", "传奇")]
    [InlineData("Bronze V", "青铜 V")]
    [InlineData("Silver IV", "白银 IV")]
    [InlineData("Gold III", "黄金 III")]
    [InlineData("Diamond II", "钻石 II")]
    [InlineData("Legend 1", "传奇 1")]
    [InlineData("Legendary #12", "传奇 #12")]
    [InlineData("Wandering", "流浪旅商")]
    [InlineData("Seasoned", "老练商贾")]
    [InlineData("Gilded", "鎏金商豪")]
    [InlineData("Apprentice Merchant", "见习商人")]
    [InlineData("Wandering Challenger", "流浪挑战者")]
    [InlineData("Seasoned Trader", "老练商贾")]
    [InlineData("Gilded Merchant", "鎏金商豪")]
    [InlineData("Diamond Grandmaster", "钻石宗师")]
    [InlineData("Legendary Champion", "传奇冠军")]
    [InlineData("Legendary of the Bazaar", "大巴扎传奇")]
    [InlineData("Gold (Normal)", "黄金")]
    [InlineData("", "见习商人")]
    [InlineData("   ", "见习商人")]
    [InlineData("传奇冠军", "传奇冠军")]
    public void FormatTitleOrRankLabel_ChineseLocale_EliminatesEnglishRanksAndTitles(string raw, string expected)
    {
        string actual = InvokeFormatTitleOrRankLabel(raw, isChineseLocale: true);
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("Bronze", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Silver", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Gold", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Diamond", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Legend", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(Normal)", actual, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Bronze")]
    [InlineData("Diamond Grandmaster")]
    [InlineData("Apprentice Merchant")]
    public void FormatTitleOrRankLabel_EnglishLocale_PreservesOriginal(string raw)
    {
        string actual = InvokeFormatTitleOrRankLabel(raw, isChineseLocale: false);
        Assert.Equal(raw, actual);
    }

    [Fact]
    public void LauncherHooks_PatchHeroBannerAndOpponentDisplayName_WithLocaleReversibility()
    {
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string l10nSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarLauncher", "Modules", "LocalizationOverlayModule.cs"));
        string tourneySrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarLauncher", "TournamentLocalizationPatch.cs"));
        string combined = l10nSrc + "\n" + tourneySrc;

        Assert.Contains("HeroBannerController", combined, StringComparison.Ordinal);
        Assert.Contains("SetHeroName", combined, StringComparison.Ordinal);
        Assert.Contains("SetHeroLabel", combined, StringComparison.Ordinal);
        Assert.Contains("GetOpponentDisplayName", combined, StringComparison.Ordinal);
        Assert.Contains("PvpNameplateFormatter", combined, StringComparison.Ordinal);
    }
}
