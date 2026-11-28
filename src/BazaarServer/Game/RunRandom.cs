using BazaarCore.Engine;

namespace BazaarServer.Game;

/// <summary>
/// 一局只有一个随机数入口：
/// - Main：跟随玩家操作的主随机数（取代 RunEngine/ShopEngine/EncounterStepEngine 各自的 _rng）；
/// - Stream：由主种子按 用途/天/小时 派生的确定性随机流，同一小时内同一用途第 N 次调用自动得到第 N 条流。
/// </summary>
public sealed class RunRandom
{
    private readonly RunState _run;
    public Random Main { get; private set; }

    public RunRandom(RunState run)
    {
        _run = run;
        Main = run.RngSeed != 0 ? new Random(run.RngSeed) : new Random(); // 与 ShopEngine 旧构造逻辑一致
    }

    /// <summary>给保留下来的旧构造重载用（例如 EncounterStepEngine(..., Random rng)）：沿用调用方传入的 Random。</summary>
    public RunRandom(RunState run, Random existingMain)
    {
        _run = run;
        Main = existingMain ?? new Random();
    }

    /// <summary>以指定种子重新开始主随机数并写进存档。</summary>
    public void ResetMain(int seed)
    {
        _run.RngSeed = seed;
        Main = new Random(seed);
    }

    /// <summary>每条命令结束时调用：让"内存里继续玩"与"从存档恢复后继续玩"走完全相同的随机序列。</summary>
    public void Checkpoint() => ResetMain(Main.Next(1, int.MaxValue));

    /// <summary>
    /// 取一条确定性随机流。第 0 次调用与旧公式 CreateStreamRng(master, tag, day, hour, index) 逐字节相同
    /// （每日挑战 / 旧整数种子的首次结果不变），之后的调用标签追加 "#N"。
    /// 旧存档 / 未调用 StartRun 的单测（MasterSeed == 0）返回 null，调用方走原逻辑。
    /// </summary>
    public Random? Stream(string tag, int index = 0)
    {
        int master = _run.MasterSeed;
        if (master == 0) return null;
        int day = (int)_run.Day, hour = (int)_run.Hour;
        int stamp = day * 100 + hour;
        if (_run.StreamOccurrenceStamp != stamp)
        {
            _run.StreamOccurrences.Clear();
            _run.StreamOccurrenceStamp = stamp;
        }
        string key = $"{tag}|{index}";
        int occ = _run.StreamOccurrences.TryGetValue(key, out int n) ? n : 0;
        _run.StreamOccurrences[key] = occ + 1;
        string effTag = occ == 0 ? tag : $"{tag}#{occ}";
        return DailyChallengeSeedGenerator.CreateStreamRng(master, effTag, day, hour, index);
    }
}
