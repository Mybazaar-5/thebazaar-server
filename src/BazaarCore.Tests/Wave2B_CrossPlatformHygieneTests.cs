using System.Reflection;
using BazaarCore.Data;
using BazaarDatasetGen;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 2B（D8-P1-07）：跨平台路径、Linux 大小写路由归一化与本机绝对路径硬编码清扫回归测试。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave2B_CrossPlatformHygieneTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Bazaar.sln"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "BazaarServer")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位仓库根目录。");
    }

    [Fact]
    public void D8_P1_07_SteamPathResolver_CrossPlatformRelativePath_NoHardcodedBackslash()
    {
        string expected = Path.Combine("The Bazaar", "TheBazaar_Data", "StreamingAssets", "GameData.db.zip");
        Assert.Equal(expected, SteamPathResolver.RelativeGameDataZip);

        // RelativeGameDataZip 须改为运行期 Path.Combine 构造的 static readonly 字段（非编译期硬编码反斜杠常量）
        FieldInfo? field = typeof(SteamPathResolver).GetField(nameof(SteamPathResolver.RelativeGameDataZip));
        Assert.NotNull(field);
        Assert.False(field!.IsLiteral, "RelativeGameDataZip 不得再使用硬编码反斜杠 const 字面量。");
        Assert.True(field.IsInitOnly, "RelativeGameDataZip 应为 static readonly。");

        string srcPath = Path.Combine(FindRepoRoot(), "src", "BazaarCore", "Data", "SteamPathResolver.cs");
        string src = File.ReadAllText(srcPath);
        Assert.DoesNotContain(@"@""The Bazaar\TheBazaar_Data\StreamingAssets\GameData.db.zip""", src, StringComparison.Ordinal);
        Assert.Contains("OperatingSystem.IsWindows()", src, StringComparison.Ordinal);
        Assert.Contains(".local", src, StringComparison.Ordinal);

        // 调用探测方法不得抛异常
        string missingDir = Path.Combine(Path.GetTempPath(), "bazaar_missing_" + Guid.NewGuid().ToString("N"));
        _ = SteamPathResolver.ResolveGameDataZipPath(missingDir);
    }

    [Fact]
    public async Task D8_P1_07_StaticDataPipeline_CaseInsensitiveRouteNormalized_AndNoClearAllPools()
    {
        // 1) 源码防线：EnsureMergedZhCnBytesFile 不得在 Pooling=false 下调用无意义的 SqliteConnection.ClearAllPools()
        string programPath = Path.Combine(FindRepoRoot(), "src", "BazaarServer", "Program.cs");
        string programSrc = File.ReadAllText(programPath);
        Assert.DoesNotContain("SqliteConnection.ClearAllPools()", programSrc, StringComparison.Ordinal);

        // 2) 中间件行为：小写路径请求 /gamedata.db.zip 与 /translations/zh-cn.bytes 须将 Request.Path 规范化为磁盘真实大小写，
        //    同时与真实大小写路径共享一致的强 SHA-256 ETag 与 304 校验
        string tempRoot = Path.Combine(Path.GetTempPath(), "bazaar_w2b_static_" + Guid.NewGuid().ToString("N"));
        try
        {
            string transDir = Path.Combine(tempRoot, "translations");
            Directory.CreateDirectory(transDir);
            string zipPath = Path.Combine(tempRoot, "GameData.db.zip");
            string zhPath = Path.Combine(transDir, "zh-CN.bytes");
            await File.WriteAllBytesAsync(zipPath, "fake-gamedata-zip-payload-v1"u8.ToArray());
            await File.WriteAllBytesAsync(zhPath, "fake-zh-cn-bytes-payload-v1"u8.ToArray());

            var services = new ServiceCollection().BuildServiceProvider();
            var appBuilder = new ApplicationBuilder(services);
            BazaarStaticDataPipeline.UseBazaarStaticDataStableEtag(appBuilder, tempRoot);

            string? observedPath = null;
            appBuilder.Run(ctx =>
            {
                observedPath = ctx.Request.Path.Value;
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            });
            RequestDelegate pipeline = appBuilder.Build();

            // 测试 /gamedata.db.zip 大小写归一 + ETag 一致 + 304
            var ctxLowerZip = new DefaultHttpContext();
            ctxLowerZip.Request.Method = HttpMethods.Get;
            ctxLowerZip.Request.Path = "/gamedata.db.zip";
            await pipeline(ctxLowerZip);
            Assert.Equal("/GameData.db.zip", observedPath);

            string expectedZipEtag = BazaarStaticDataPipeline.ComputeCachedFileSha256Etag(zipPath);
            var ctxZip304 = new DefaultHttpContext();
            ctxZip304.Request.Method = HttpMethods.Get;
            ctxZip304.Request.Path = "/gamedata.db.zip";
            ctxZip304.Request.Headers.IfNoneMatch = expectedZipEtag;
            await pipeline(ctxZip304);
            Assert.Equal(StatusCodes.Status304NotModified, ctxZip304.Response.StatusCode);
            Assert.Equal(expectedZipEtag, ctxZip304.Response.Headers.ETag.ToString());

            // 测试 /translations/zh-cn.bytes 大小写归一 + 304
            observedPath = null;
            var ctxLowerZh = new DefaultHttpContext();
            ctxLowerZh.Request.Method = HttpMethods.Get;
            ctxLowerZh.Request.Path = "/translations/zh-cn.bytes";
            await pipeline(ctxLowerZh);
            Assert.Equal("/translations/zh-CN.bytes", observedPath);

            string expectedZhEtag = BazaarStaticDataPipeline.ComputeCachedFileSha256Etag(zhPath);
            var ctxZh304 = new DefaultHttpContext();
            ctxZh304.Request.Method = HttpMethods.Get;
            ctxZh304.Request.Path = "/translations/zh-cn.bytes";
            ctxZh304.Request.Headers.IfNoneMatch = expectedZhEtag;
            await pipeline(ctxZh304);
            Assert.Equal(StatusCodes.Status304NotModified, ctxZh304.Response.StatusCode);
            Assert.Equal(expectedZhEtag, ctxZh304.Response.Headers.ETag.ToString());
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void D8_P1_07_CliAndDatasetGen_ZeroLocalAbsolutePathHardcoding()
    {
        string legacyWorkspace = string.Concat("e:", "\\", "the", "bazaar");
        var opts = new DatasetGenOptions();
        Assert.Equal(Path.Combine("output", "pvp_dataset"), opts.OutputDir);
        Assert.False(Path.IsPathRooted(opts.OutputDir), "默认 OutputDir 须为相对路径。");
        Assert.DoesNotContain(legacyWorkspace, opts.OutputDir, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(legacyWorkspace, DatasetGenOptions.GetHelpText(), StringComparison.OrdinalIgnoreCase);

        string repoRoot = FindRepoRoot();
        string datasetGenSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarDatasetGen", "DatasetGenOptions.cs"));
        string smartBotSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarCli", "SmartBot.cs"));

        Assert.DoesNotContain(legacyWorkspace, datasetGenSrc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(legacyWorkspace, smartBotSrc, StringComparison.OrdinalIgnoreCase);
    }
}
