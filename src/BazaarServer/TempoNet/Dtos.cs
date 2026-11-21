using System.Text.Json;
using System.Text.Json.Serialization;

namespace BazaarServer.TempoNet;

// ─────────────────────────────────────────────────────────────────────────────
// tempoNet 兼容层 DTO。字段名与官方 JSON 逐字对齐（Newtonsoft 默认 PascalCase→原样，
// 官方 DTO 用的是 PascalCase 属性名，客户端用 Newtonsoft 反序列化，大小写不敏感）。
// 全部用可空 + 默认值，缺字段不会炸。
// ─────────────────────────────────────────────────────────────────────────────

public sealed class LoginResponseDto
{
    public string AccessToken { get; set; }
    public string RefreshToken { get; set; }
    public string AlternateRefreshToken { get; set; }
    public DateTime AccessTokenExpiresAt { get; set; }
    public DateTime CurrentTime { get; set; }
}

public sealed class RefreshTokenResponseDto
{
    public string AccessToken { get; set; }
    public string RefreshToken { get; set; }
    public DateTime AccessTokenExpiresAt { get; set; }
    public DateTime CurrentTime { get; set; }
}

public sealed class AccountSummaryDto
{
    public string UserName { get; set; }
    public string Username { get; set; }
    public string Email { get; set; }
    public bool IsEmailVerified { get; set; }
    public Guid AccountId { get; set; }
    public string CountryCode { get; set; }
    public bool HasViewedDailySpecial { get; set; }
}

/// <summary>
/// 对应官方 FeatureAccessResponse（readonly record struct）：
/// { accountId, chestPurchases, prizePasses, subscriptions }
/// 注意：字段名与官方完全一致，且 accountId 不能为空——ProfileCache.Update 会校验。
/// </summary>
public sealed class FeatureAccessDto
{
    public Guid accountId { get; set; }
    public bool chestPurchases { get; set; } = true;
    public bool prizePasses { get; set; } = true;
    public bool subscriptions { get; set; } = true;
}

/// <summary>
/// 对应官方 PlayerProfileCareerResponse：{ AccountId, HasCompletedTutorial, SeasonRanks, Heroes }
/// ProfileCache.Update 会校验 AccountId 非空。
/// </summary>
public sealed class PlayerProfileCareerDto
{
    public Guid AccountId { get; set; }
    public bool HasCompletedTutorial { get; set; } = true;
    public Dictionary<string, object> SeasonRanks { get; set; } = new();
    public object[] Heroes { get; set; } = Array.Empty<object>();
}

public sealed class PlayerProfileDto
{
    public Guid AccountId { get; set; }
    public string Username { get; set; }
    public string TitlePrefix { get; set; }
    public string TitleSuffix { get; set; }
    public RankDto SeasonRank { get; set; }
    public object ActiveRun { get; set; }
}

public sealed class RankDto
{
    public string SeasonId { get; set; } = "2";
    public int Rank { get; set; } = 0;
    public int Division { get; set; } = 1;
    public int Rating { get; set; } = 0;
}

public sealed class PlayerRankDto
{
    public Guid AccountId { get; set; }
    public int SeasonID { get; set; } = 2;
    public int Rank { get; set; }
    public int Division { get; set; } = 1;
    public int RankPoints { get; set; }
    public int Rating { get; set; }
}

public sealed class WalletDto
{
    public Guid AccountId { get; set; }
    public int Gems { get; set; }
    public int RankedVouchers { get; set; }
    public int DailyRankedVouchers { get; set; }
}

public sealed class SeasonDto
{
    public int id { get; set; }
    public string name { get; set; }
    public string title { get; set; }
    public DateTime start { get; set; }
    public int allowBundleChestAfterSeasonId { get; set; }
}

public sealed class SeasonTrackDto
{
    public int seasonId { get; set; }
    public object[] tiers { get; set; } = Array.Empty<object>();
    public object infiniteTier { get; set; }
    public int subscriptionXpMultiplier { get; set; } = 1;
    public CurrencyDto expansionPrice { get; set; }
    public CurrencyDto tierSkipPrice { get; set; }
}

public sealed class SeasonTrackProgressionDto
{
    public Guid accountId { get; set; }
    public int seasonId { get; set; } = 2;
    public bool isPaid { get; set; }
    public int experience { get; set; }
    public int[] claimedFreeRewardTiers { get; set; } = Array.Empty<int>();
    public int[] claimedPaidRewardTiers { get; set; } = Array.Empty<int>();
    public int claimedFreeInfiniteTierCount { get; set; }
    public int claimedPaidInfiniteTierCount { get; set; }
}

public sealed class CurrencyDto
{
    public int CurrencyType { get; set; }
    public int Amount { get; set; }
}

public sealed class RunRewardLevelDto
{
    public int WinThreshold { get; set; }
    public int UnrankedChestCount { get; set; }
    public int RankedChestCount { get; set; }
    public int SubscriptionMultiplier { get; set; } = 2;
    public CurrencyDto[] UnrankedCurrencyRewards { get; set; } = Array.Empty<CurrencyDto>();
    public CurrencyDto[] RankedCurrencyRewards { get; set; } = Array.Empty<CurrencyDto>();
}

public sealed class PostRunBootstrapResponseDto
{
    public WalletDto wallet { get; set; }
    public ChallengesDto challenges { get; set; } = new();
    public PlayerProfileDto profile { get; set; }
    public FeatureAccessDto featureAccess { get; set; }
    public AccountSummaryDto accountSummary { get; set; }
    public SeasonTrackDto seasonTrack { get; set; }
    public SeasonTrackProgressionDto seasonTrackProgression { get; set; }
    public UnopenedChestsDto chests { get; set; } = new();
    public object[] errors { get; set; } = Array.Empty<object>();
    public bool hasErrors { get; set; }
}

public sealed class HeroLoadoutDto
{
    public string accountId { get; set; }
    public string heroId { get; set; }
    public string boardId { get; set; }
    public string carpetId { get; set; }
    public string cardBackId { get; set; }
    public string toyId { get; set; }
    public string stashId { get; set; }
    public string bankId { get; set; }
    public string albumId { get; set; }
    public string heroSkinId { get; set; }
    public string[] cardSkinIds { get; set; } = Array.Empty<string>();
    public bool randomizeLoadout { get; set; }
}

public sealed class CollectionLoadoutDto
{
    public string accountId { get; set; }
    public string boardId { get; set; } = "";
    public string carpetId { get; set; } = "";
    public string cardBackId { get; set; } = "";
    public string toyId { get; set; } = "";
    public string stashId { get; set; } = "";
    public string bankId { get; set; } = "";
    public string albumId { get; set; } = "";
    public string[] heroSkinIds { get; set; } = Array.Empty<string>();
    public string[] cardSkinIds { get; set; } = Array.Empty<string>();
}

public sealed class EquipLoadoutRequestDto
{
    public string? boardId { get; set; }
    public string? carpetId { get; set; }
    public string? cardBackId { get; set; }
    public string? toyId { get; set; }
    public string? stashId { get; set; }
    public string? bankId { get; set; }
    public string? albumId { get; set; }
    public string? heroSkinId { get; set; }
}

public sealed class HeroLoadoutsResponseDto
{
    public List<HeroLoadoutDto> loadouts { get; set; } = new();
    public CollectionLoadoutDto collection { get; set; } = new();
}

public sealed class OwnedHeroDto
{
    public string heroId { get; set; }
    public DateTime releaseDate { get; set; }
    public bool owned { get; set; } = true;
    public string unlockedAt { get; set; }
}

public sealed class ClientHeroListingDto
{
    public string heroId { get; set; } = "";
    public DateTime releaseDate { get; set; }
    public int price { get; set; }
    public bool isPurchasable { get; set; }
}

public sealed class HeroListingsDto
{
    public string accountId { get; set; } = "";
    public int purchaseCount { get; set; }
    public float[] discountRates { get; set; } = Array.Empty<float>();
    public ClientHeroListingDto[] heroes { get; set; } = Array.Empty<ClientHeroListingDto>();
}

public sealed class PurchaseHeroListingsDto
{
    public object[] Listings { get; set; } = Array.Empty<object>();
}

public sealed class CollectionItemResponseDto
{
    public Guid id { get; set; }
    public Guid itemId { get; set; }
    public int? mintNumber { get; set; }
}

public sealed class ChestResponseDto
{
    public Guid id { get; set; }
    public Guid? ownerId { get; set; }
    public int seasonId { get; set; }
    public List<CurrencyDto> currencies { get; set; } = new();
    public CollectionItemResponseDto item { get; set; } = new();
    public DateTime? openedAt { get; set; }
    public Guid? parentId { get; set; }
    public List<Guid> children { get; set; } = new();
    public int gemsAwarded { get; set; }
    public bool isDuplicate { get; set; }
}

public sealed class UnopenedChestsDto
{
    public int bundledUnopenedCount { get; set; }
    public ChestResponseDto[] legacyChests { get; set; } = Array.Empty<ChestResponseDto>();
}

public sealed class ClientChestCurrencyDto
{
    public string CurrencyType { get; set; } = "Gem";
    public int Amount { get; set; }
}

public sealed class ClientChestOpenResponseDto
{
    public int seasonId { get; set; }
    public Guid? collectionItemId { get; set; }
    public DateTime openedAt { get; set; }
    public bool isDuplicate { get; set; }
    public int gemsAwarded { get; set; }
    public Guid id { get; set; }
    public Guid? ownerId { get; set; }
    public Guid? parentId { get; set; }
    public ClientChestCurrencyDto[] currencies { get; set; } = Array.Empty<ClientChestCurrencyDto>();
    public Guid[] children { get; set; } = Array.Empty<Guid>();
    public ClientChestCurrencyDto? currency { get; set; }
    public bool awardedChildChest { get; set; }
}

public sealed class BundledChestsGrantDto
{
    public int count { get; set; }
    public int seasonId { get; set; }
}

public sealed class StateDeltaResponseDto
{
    public WalletDto? Wallet { get; set; }
    public SeasonTrackProgressionDto? SeasonTrackProgression { get; set; }
    public PlayerRankDto? PlayerRank { get; set; }
    public ChallengesDto? Challenges { get; set; }
    public OwnedHeroDto[]? OwnedHeroesAdded { get; set; }
    public Guid[]? CollectionItemsAdded { get; set; }
    public ChestResponseDto[]? ChestsAdded { get; set; }
    public object[]? OwnedTitlePrefixesAdded { get; set; }
    public object[]? OwnedTitleSuffixesAdded { get; set; }
    public BundledChestsGrantDto? BundledChestsGranted { get; set; }
    public Guid[]? CollectionItemsRemoved { get; set; }
    public Guid[]? ChestsRemoved { get; set; }
    public int[]? PendingRewardsRemoved { get; set; }
}

public sealed class AppOpenChestsResponseDto
{
    public ClientChestOpenResponseDto[] Chests { get; set; } = Array.Empty<ClientChestOpenResponseDto>();
    public StateDeltaResponseDto State { get; set; } = new();
}

public sealed class AppStorePurchaseChestsResponseDto
{
    public Guid accountId { get; set; }
    public ClientChestCurrencyDto cost { get; set; } = new();
    public StateDeltaResponseDto State { get; set; } = new();
}

public sealed class ChestDropRatesResponseDto
{
    public int seasonId { get; set; }
    public object[] itemRarities { get; set; } = Array.Empty<object>();
    public object[] currencies { get; set; } = Array.Empty<object>();
    public object[] bonuses { get; set; } = Array.Empty<object>();
    public Dictionary<string, object[]> lootTables { get; set; } = new();
}

public sealed class DailySpecialsDto
{
    public object[] listings { get; set; } = Array.Empty<object>();
}

public sealed class CurrencyListingsDto
{
    public object[] currencyListings { get; set; } = Array.Empty<object>();
}

public sealed class ChallengesDto
{
    public object[] challenges { get; set; } = Array.Empty<object>();
    public object[] dailyChallenges { get; set; } = Array.Empty<object>();
    public object[] weeklyChallenges { get; set; } = Array.Empty<object>();
    public int dailyResets { get; set; }
    public int weeklyResets { get; set; }
    public DateTime dailyChallengesExpireAt { get; set; } = DateTime.UtcNow.AddDays(1);
    public DateTime weeklyChallengesExpireAt { get; set; } = DateTime.UtcNow.AddDays(7);
}

public sealed class LeaderboardPositionDto
{
    public int position { get; set; }
    public int total { get; set; }
    public int rating { get; set; }
}

// BootstrapResponse：字段名必须与客户端 Core.Data.Models.BootstrapResponse 一致（小写开头）
public sealed class BootstrapResponseDto
{
    public DateTime? serverTime { get; set; }
    public BootstrapAccountDto account { get; set; } = new();
    public BootstrapProfileDto profile { get; set; } = new();
    public WalletDto wallet { get; set; }
    public PlayerRankDto playerRank { get; set; }
    public BootstrapSeasonDto season { get; set; } = new();
    public ChallengesDto challenges { get; set; }
    public BootstrapHeroesDto heroes { get; set; } = new();
    public BootstrapCollectionDto collection { get; set; } = new();
    public UnopenedChestsDto chests { get; set; } = new();
    public BootstrapMarketplaceDto marketplace { get; set; } = new();
    public LeaderboardPositionDto leaderboard { get; set; }
    public object[] pendingRewards { get; set; } = Array.Empty<object>();
    public object[] runRewardLevels { get; set; } = Array.Empty<object>();
    public object[] errors { get; set; } = Array.Empty<object>();
    public bool hasErrors { get; set; }
}

public sealed class BootstrapAccountDto
{
    public AccountSummaryDto summary { get; set; } = new();
    public FeatureAccessDto featureAccess { get; set; } = new();
}

public sealed class BootstrapProfileDto
{
    public PlayerProfileDto playerProfile { get; set; } = new();
}

public sealed class BootstrapSeasonDto
{
    public SeasonDto current { get; set; }
    public SeasonDto[] all { get; set; } = Array.Empty<SeasonDto>();
    public SeasonTrackDto track { get; set; }
    public SeasonTrackProgressionDto trackProgression { get; set; }
}

public sealed class BootstrapHeroesDto
{
    public OwnedHeroDto[] owned { get; set; } = Array.Empty<OwnedHeroDto>();
    public HeroListingsDto listings { get; set; } = new();
    public PurchaseHeroListingsDto purchaseListings { get; set; } = new();
}

public sealed class BootstrapCollectionDto
{
    public Guid[] items { get; set; } = Array.Empty<Guid>();
    public HeroLoadoutsResponseDto loadouts { get; set; } = new();
}

public sealed class BootstrapMarketplaceDto
{
    public DailySpecialsDto dailySpecials { get; set; } = new();
    public CurrencyListingsDto currencyListings { get; set; } = new();
}
