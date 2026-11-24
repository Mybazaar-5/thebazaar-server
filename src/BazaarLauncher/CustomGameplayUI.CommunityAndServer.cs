using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BazaarLauncher.Core;
using TMPro;
using UnityEngine;

namespace BazaarLauncher;

/// <summary>
/// F8 面板扩展：多服务器选择与测速、接收服务器 Mod 开关、社区 Mod 浏览/下载/上传、
/// 以及锦标赛房主选 Mod 与成员一键同步/下载。
/// </summary>
internal sealed partial class CustomGameplayUI
{
    // ── 服务器切换区控件 ──────────────────────────────────────────────────────
    private TextMeshProUGUI _srvCurrentUrlText;
    private NativeUguiFactory.ToggleHandle _togAcceptServerMods;
    private Transform _srvProfilesContainer;
    private TMP_InputField _srvAddNameInput;
    private TMP_InputField _srvAddUrlInput;
    private readonly Dictionary<string, string> _srvPingResults = new(StringComparer.OrdinalIgnoreCase);

    // ── 社区 Mod 与本地上传区控件 ──────────────────────────────────────────────
    private TMP_InputField _communitySearchInput;
    private TextMeshProUGUI _communityStatusText;
    private Transform _communityListContainer;
    private Transform _localUploadListContainer;
    private TMP_InputField _manualUploadPathInput;
    private bool _communityFetchInFlight;

    // ── 锦标赛房间 Mod 选择与成员同步区控件 ───────────────────────────────────
    private GameObject _tourneyRoomModsBannerCard;
    private TextMeshProUGUI _tourneyRoomModsStatusText;
    private Transform _tourneyRoomModsListContainer;
    private Transform _tourneyHostModPickerContainer;
    private string _lastAutoSyncedRoomModSignature = string.Empty;

    private void BuildServerAndCommunitySections(Transform workshopParent)
    {
        // 1. 服务器管理卡片
        var srvCard = _ui.CreateCard("ServerManageCard", workshopParent, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 10, spacing: 7f);
        var srvHdr = _ui.CreateHorizontalRow("SrvHdr", srvCard.transform, spacing: 8f);
        var srvTitle = _ui.CreateText("Title", srvHdr.transform, "服务器切换与 Mod 接收设置", 15.5f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(srvTitle.gameObject, flexW: 1f, minH: 26f);

        var btnPingAll = _ui.CreateButton("BtnPingAll", srvHdr.transform, "全部测速", 12.5f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream,
            PingAllSavedServersAsync, NativeUguiFactory.ColGoldDim);
        NativeUguiFactory.SetLayout(btnPingAll.Root, minW: 88f, prefW: 94f, minH: 26f);

        _srvCurrentUrlText = _ui.CreateText("CurrentSrv", srvCard.transform, "", 13f, NativeUguiFactory.ColCream);

        bool acceptInit = BazaarLauncherPlugin.AcceptServerMods == null || BazaarLauncherPlugin.AcceptServerMods.Value;
        _togAcceptServerMods = _ui.CreateToggle("TogAcceptServerMods", srvCard.transform,
            "允许接收当前服务器下发的 Mod 贴图与文案（关闭后不加载服务器下发的 Mod）",
            acceptInit,
            v =>
            {
                BazaarLauncherPlugin.SetAcceptServerMods(v);
                SetStatus(v ? "已开启接收服务器 Mod，正在重新同步" : "已关闭接收服务器 Mod，已清掉同步覆盖", isError: false);
                RefreshServerAndCommunityUi();
            });

        _srvProfilesContainer = _ui.CreateVerticalGroup("SrvProfilesList", srvCard.transform, spacing: 4f).transform;

        var addSrvRow = _ui.CreateHorizontalRow("AddSrvRow", srvCard.transform, spacing: 6f);
        _srvAddNameInput = _ui.CreateInputField("SrvName", addSrvRow.transform, "名称（如 好友服）", "", fontSize: 13f, charLimit: 24);
        NativeUguiFactory.SetLayout(_srvAddNameInput.gameObject, minW: 130f, prefW: 145f, minH: 28f, prefH: 28f);
        _srvAddUrlInput = _ui.CreateInputField("SrvUrl", addSrvRow.transform, "地址（如 http://203.0.113.10:8080）", "", fontSize: 13f, charLimit: 120);
        NativeUguiFactory.SetLayout(_srvAddUrlInput.gameObject, minW: 220f, flexW: 1f, minH: 28f, prefH: 28f);
        var btnAddSwitch = _ui.CreateButton("BtnAddSwitch", addSrvRow.transform, "保存并切换", 12.5f,
            NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, () =>
            {
                string nm = _srvAddNameInput?.text?.Trim() ?? string.Empty;
                string url = _srvAddUrlInput?.text?.Trim() ?? string.Empty;
                if (!ServerProfileStore.AddOrUpdate(nm, url, out string normalized, out string err))
                {
                    SetStatus(err, isError: true);
                    return;
                }
                SwitchToServerUrl(normalized, nm);
            }, NativeUguiFactory.ColGold);
        NativeUguiFactory.SetLayout(btnAddSwitch.Root, minW: 102f, prefW: 108f, minH: 28f);

        // 2. 社区 Mod 浏览与下载卡片
        var commCard = _ui.CreateCard("CommunityModsCard", workshopParent, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 10, spacing: 7f);
        var commHdr = _ui.CreateHorizontalRow("CommHdr", commCard.transform, spacing: 6f);
        var commTitle = _ui.CreateText("Title", commHdr.transform, "服务器社区 Mod（下载到本地或建房选用）", 15.5f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(commTitle.gameObject, flexW: 1f, minH: 26f);

        _communitySearchInput = _ui.CreateInputField("CommSearch", commHdr.transform, "按名称/作者筛选...", "", fontSize: 12.5f, charLimit: 40);
        NativeUguiFactory.SetLayout(_communitySearchInput.gameObject, minW: 150f, prefW: 170f, minH: 26f, prefH: 26f);

        var btnRefreshComm = _ui.CreateButton("BtnRefreshComm", commHdr.transform, "刷新列表", 12.5f,
            NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white,
            () => RefreshCommunityModsAsync(force: true), NativeUguiFactory.ColGold);
        NativeUguiFactory.SetLayout(btnRefreshComm.Root, minW: 88f, prefW: 94f, minH: 26f);

        _communityStatusText = _ui.CreateText("CommStatus", commCard.transform, "点击「刷新列表」获取当前服务器的社区 Mod。", 12.5f, NativeUguiFactory.ColMuted);
        _communityListContainer = _ui.CreateVerticalGroup("CommunityModsList", commCard.transform, spacing: 4f).transform;

        // 3. 上传本地 Mod 到服务器卡片
        var upCard = _ui.CreateCard("UploadLocalModsCard", workshopParent, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 10, spacing: 7f);
        var upHdr = _ui.CreateHorizontalRow("UpHdr", upCard.transform, spacing: 8f);
        var upTitle = _ui.CreateText("Title", upHdr.transform, "上传本地 Mod 到当前服务器（通过校验后所有人可用）", 15f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(upTitle.gameObject, flexW: 1f, minH: 26f);

        var btnRescanLocal = _ui.CreateButton("BtnRescanLocal", upHdr.transform, "重新扫描本地", 12.5f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream,
            RebuildLocalModUploadList, NativeUguiFactory.ColGoldDim);
        NativeUguiFactory.SetLayout(btnRescanLocal.Root, minW: 108f, prefW: 114f, minH: 26f);

        _ui.CreateText("UpHint", upCard.transform,
            $"自动扫描目录：{ModContentSyncClient.LocalModsDownloadRoot} 及游戏目录 mods/（也支持粘贴任意本地 Mod 目录或 .bazaarmod.zip 路径）。",
            12f, NativeUguiFactory.ColMuted);
        _localUploadListContainer = _ui.CreateVerticalGroup("LocalModsList", upCard.transform, spacing: 4f).transform;

        var manualRow = _ui.CreateHorizontalRow("ManualUploadRow", upCard.transform, spacing: 6f);
        _manualUploadPathInput = _ui.CreateInputField("ManualPath", manualRow.transform, "本地 Mod 目录路径或 .bazaarmod.zip 完整路径...", "", fontSize: 12.5f, charLimit: 260);
        NativeUguiFactory.SetLayout(_manualUploadPathInput.gameObject, minW: 260f, flexW: 1f, minH: 28f, prefH: 28f);
        var btnManualUpload = _ui.CreateButton("BtnManualUpload", manualRow.transform, "打包并上传", 12.5f,
            NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, () =>
            {
                string path = _manualUploadPathInput?.text?.Trim() ?? string.Empty;
                UploadLocalModPathAsync(path);
            }, NativeUguiFactory.ColGold);
        NativeUguiFactory.SetLayout(btnManualUpload.Root, minW: 100f, prefW: 108f, minH: 28f);
    }

    private void BuildTournamentRoomModBanner(Transform parent)
    {
        _tourneyRoomModsBannerCard = _ui.CreateCard("TourneyRoomModsBanner", parent, NativeUguiFactory.ColAmberBg, NativeUguiFactory.ColGold, padH: 12, padV: 8, spacing: 6f);
        var hdr = _ui.CreateHorizontalRow("Hdr", _tourneyRoomModsBannerCard.transform, spacing: 8f);
        var title = _ui.CreateText("Title", hdr.transform, "本房使用 Mod（所有成员开赛自动生效）", 14.5f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(title.gameObject, flexW: 1f, minH: 26f);

        var btnSyncRoomMods = _ui.CreateButton("BtnSyncRoomMods", hdr.transform, "同步本房 Mod", 12.5f,
            NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, () =>
            {
                SyncCurrentLobbyRequiredModsAsync(force: true);
            }, NativeUguiFactory.ColGold);
        NativeUguiFactory.SetLayout(btnSyncRoomMods.Root, minW: 112f, prefW: 118f, minH: 26f);

        var btnDownloadAllRoomMods = _ui.CreateButton("BtnDownloadAllRoomMods", hdr.transform, "全部下载到本地", 12.5f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
            {
                DownloadAllCurrentLobbyModsAsync();
            }, NativeUguiFactory.ColGoldDim);
        NativeUguiFactory.SetLayout(btnDownloadAllRoomMods.Root, minW: 118f, prefW: 126f, minH: 26f);

        _tourneyRoomModsStatusText = _ui.CreateText("RoomModsStatus", _tourneyRoomModsBannerCard.transform, "", 12.5f, NativeUguiFactory.ColCream);
        _tourneyRoomModsListContainer = _ui.CreateVerticalGroup("RoomModsList", _tourneyRoomModsBannerCard.transform, spacing: 4f).transform;
        _tourneyRoomModsBannerCard.SetActive(false);
    }

    private void BuildTournamentHostModPickerCard(Transform parent)
    {
        var card = _ui.CreateCard("TourneyHostModPicker", parent, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 10, spacing: 6f);
        var hdr = _ui.CreateHorizontalRow("Hdr", card.transform, spacing: 8f);
        var title = _ui.CreateText("Title", hdr.transform, "本房选用 Mod（房主勾选后点保存即可，成员进房自动同步）", 14.5f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(title.gameObject, flexW: 1f, minH: 26f);

        var btnRefresh = _ui.CreateButton("BtnRefreshHostMods", hdr.transform, "刷新可选 Mod", 12.5f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream,
            () => RefreshCommunityModsAsync(force: true), NativeUguiFactory.ColGoldDim);
        NativeUguiFactory.SetLayout(btnRefresh.Root, minW: 108f, prefW: 116f, minH: 26f);

        _tourneyHostModPickerContainer = _ui.CreateVerticalGroup("HostModPickerList", card.transform, spacing: 4f).transform;
    }

    private string _autoSyncRoomModInFlightSig = string.Empty;

    /// <summary>热切服后在主线程彻底清理旧服缓存状态、断开旧 SSE 并重新拉取新服配置与社区列表。</summary>
    internal void OnServerBaseSwitched()
    {
        StopSocialSse();
        _catalogLoaded = false;
        _currentLobby = null;
        _lobbyDirty = false;
        _lastAutoSyncedRoomModSignature = string.Empty;
        _autoSyncRoomModInFlightSig = string.Empty;
        _autoAppliedLobbyIds.Clear();
        _socialMessages.Clear();
        System.Threading.Interlocked.Exchange(ref _lastSocialEventId, 0L);

        RefreshServerAndCommunityUi();
        StartInitialBackgroundFetch();
        RefreshCommunityModsAsync(force: true);
        SyncSocialSseForLobbyOrPanelState();
        RefreshHudOverlays(force: true);
        if (_isOpen)
        {
            RefreshModalContentsIfOpen();
        }
    }

    private void SwitchToServerUrl(string url, string displayName = null)
    {
        if (!BazaarLauncherPlugin.TrySwitchServerBase(url, out string normalized, out string err))
        {
            SetStatus(err, isError: true);
            return;
        }

        string label = string.IsNullOrWhiteSpace(displayName) ? normalized : $"{displayName}（{normalized}）";
        SetStatus($"已切换服务器到 {label}（若已在对局中，请回主界面或重启游戏生效）", isError: false);
        ShowToastBanner($"已切换服务器：{label}");
    }

    private void PingAllSavedServersAsync()
    {
        var profiles = ServerProfileStore.LoadAll();
        SetStatus("正在测速...", isError: false);
        _ = Task.Run(async () =>
        {
            foreach (var p in profiles)
            {
                var (online, _, info) = await ServerProfileStore.PingServerAsync(p.Url, HttpClientHub.ShutdownToken).ConfigureAwait(false);
                lock (_srvPingResults)
                {
                    _srvPingResults[ServerProfileStore.NormalizeUrl(p.Url)] = online ? $"在线 {info}" : info;
                }
            }
            _pendingUpdates.Enqueue(() =>
            {
                SetStatus("服务器测速完成", isError: false);
                RefreshServerProfilesUi();
            });
        });
    }

    private void RefreshServerAndCommunityUi()
    {
        RefreshServerProfilesUi();
        RebuildLocalModUploadList();
        RebuildCommunityModListUi();
        if (ModContentSyncClient.Instance.GetCommunityModsSnapshot().Count == 0 && !_communityFetchInFlight)
        {
            RefreshCommunityModsAsync(force: false);
        }
    }

    private void RefreshServerProfilesUi()
    {
        string curUrl = ServerProfileStore.NormalizeUrl(NameAuth.GetServerBaseUrl());
        if (_srvCurrentUrlText != null)
        {
            _srvCurrentUrlText.text = $"当前服务器：{(string.IsNullOrEmpty(curUrl) ? "未配置" : curUrl)}";
        }
        if (_togAcceptServerMods != null && BazaarLauncherPlugin.AcceptServerMods != null)
        {
            _togAcceptServerMods.Toggle.SetIsOnWithoutNotify(BazaarLauncherPlugin.AcceptServerMods.Value);
        }
        if (_srvProfilesContainer == null) return;

        ClearChildren(_srvProfilesContainer);
        var profiles = ServerProfileStore.LoadAll();
        foreach (var p in profiles)
        {
            var captured = p;
            string norm = ServerProfileStore.NormalizeUrl(captured.Url);
            bool isCurrent = string.Equals(norm, curUrl, StringComparison.OrdinalIgnoreCase);
            string pingText;
            lock (_srvPingResults)
            {
                _srvPingResults.TryGetValue(norm, out pingText);
            }
            string statusTag = string.IsNullOrEmpty(pingText) ? "" : $" [{pingText}]";
            string prefix = isCurrent ? "[当前] " : "";

            var row = _ui.CreateHorizontalRow("SrvRow_" + norm.GetHashCode(), _srvProfilesContainer, spacing: 6f);
            var lbl = _ui.CreateText("Lbl", row.transform,
                $"{prefix}{captured.Name}  {captured.Url}{statusTag}",
                12.5f,
                isCurrent ? NativeUguiFactory.ColEmeraldText : NativeUguiFactory.ColCream,
                wrap: true);
            NativeUguiFactory.SetLayout(lbl.gameObject, flexW: 1f, minH: 24f);

            var btnPing = _ui.CreateButton("Ping", row.transform, "测速", 12f,
                NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
                {
                    _ = Task.Run(async () =>
                    {
                        var (online, _, info) = await ServerProfileStore.PingServerAsync(captured.Url, HttpClientHub.ShutdownToken).ConfigureAwait(false);
                        lock (_srvPingResults) { _srvPingResults[norm] = online ? $"在线 {info}" : info; }
                        _pendingUpdates.Enqueue(RefreshServerProfilesUi);
                    });
                }, padH: 8, padV: 2);
            NativeUguiFactory.SetLayout(btnPing.Root, minW: 54f, prefW: 58f, minH: 24f);

            if (!isCurrent)
            {
                var btnUse = _ui.CreateButton("Use", row.transform, "切换", 12f,
                    NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white,
                    () => SwitchToServerUrl(captured.Url, captured.Name), padH: 8, padV: 2);
                NativeUguiFactory.SetLayout(btnUse.Root, minW: 54f, prefW: 58f, minH: 24f);
            }

            if (!isCurrent && !string.Equals(norm, "http://127.0.0.1:8080", StringComparison.OrdinalIgnoreCase))
            {
                var btnDel = _ui.CreateButton("Del", row.transform, "删", 12f,
                    NativeUguiFactory.ColDangerBg, NativeUguiFactory.ColBtnHover, Color.white, () =>
                    {
                        ServerProfileStore.Remove(captured.Url);
                        RefreshServerProfilesUi();
                    }, padH: 6, padV: 2);
                NativeUguiFactory.SetLayout(btnDel.Root, minW: 36f, prefW: 38f, minH: 24f);
            }
        }
    }

    private void RefreshCommunityModsAsync(bool force)
    {
        if (_communityFetchInFlight) return;
        _communityFetchInFlight = true;
        string search = _communitySearchInput?.text?.Trim() ?? string.Empty;
        if (_communityStatusText != null) _communityStatusText.text = "正在获取服务器社区 Mod 列表...";

        _ = Task.Run(async () =>
        {
            var (ok, mods, _, err) = await ModContentSyncClient.Instance
                .FetchCommunityModsAsync(search, HttpClientHub.ShutdownToken).ConfigureAwait(false);
            _pendingUpdates.Enqueue(() =>
            {
                _communityFetchInFlight = false;
                if (_communityStatusText != null)
                {
                    _communityStatusText.text = ok
                        ? $"共 {mods.Count} 个可用 Mod（含全服启用与社区上传）"
                        : "获取社区 Mod 列表失败：" + err;
                }
                RebuildCommunityModListUi();
                RefreshTournamentHostModPickerUi();
            });
        });
    }

    private void RebuildCommunityModListUi()
    {
        if (_communityListContainer == null) return;
        ClearChildren(_communityListContainer);

        var mods = ModContentSyncClient.Instance.GetCommunityModsSnapshot();
        if (mods.Count == 0)
        {
            _ui.CreateText("Empty", _communityListContainer, "当前服务器还没有社区 Mod，可以在下方上传你的本地 Mod。", 12.5f, NativeUguiFactory.ColMuted);
            return;
        }

        int limit = Math.Min(mods.Count, 24);
        for (int i = 0; i < limit; i++)
        {
            var captured = mods[i];
            var row = _ui.CreateHorizontalRow("CommMod_" + captured.ModId, _communityListContainer, spacing: 6f);
            string tag = captured.IsServerDefault ? "[全服启用]" : "[社区]";
            string author = string.IsNullOrWhiteSpace(captured.UploaderName) ? captured.Author : captured.UploaderName;
            var txt = _ui.CreateText("Txt", row.transform,
                $"{tag} {captured.Name} v{captured.Version} · 作者 {author} · 卡 {captured.CustomCardCount} / 事件 {captured.CustomEventCount} / 下载 {captured.DownloadCount}",
                12.5f,
                captured.IsServerDefault ? NativeUguiFactory.ColEmeraldText : NativeUguiFactory.ColCream,
                wrap: true);
            NativeUguiFactory.SetLayout(txt.gameObject, flexW: 1f, minH: 24f);

            var btnDl = _ui.CreateButton("Dl", row.transform, "下载到本地", 12f,
                NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, () =>
                {
                    SetStatus($"正在下载「{captured.Name}」...", isError: false);
                    _ = Task.Run(async () =>
                    {
                        var (ok, msg, _, _) = await ModContentSyncClient.Instance
                            .DownloadModToLocalAsync(captured.ModId, HttpClientHub.ShutdownToken).ConfigureAwait(false);
                        _pendingUpdates.Enqueue(() =>
                        {
                            SetStatus(msg, !ok);
                            if (ok)
                            {
                                ShowToastBanner($"已下载 Mod：{captured.Name}");
                                RebuildLocalModUploadList();
                            }
                        });
                    });
                }, padH: 8, padV: 2);
            NativeUguiFactory.SetLayout(btnDl.Root, minW: 86f, prefW: 92f, minH: 24f);

            if (captured.CanDelete)
            {
                var btnDel = _ui.CreateButton("Del", row.transform, "删除", 12f,
                    NativeUguiFactory.ColDangerBg, NativeUguiFactory.ColBtnHover, Color.white, () =>
                    {
                        _ = Task.Run(async () =>
                        {
                            var (ok, msg) = await ModContentSyncClient.Instance
                                .DeleteCommunityModAsync(captured.ModId, HttpClientHub.ShutdownToken).ConfigureAwait(false);
                            _pendingUpdates.Enqueue(() =>
                            {
                                SetStatus(msg, !ok);
                                if (ok) RefreshCommunityModsAsync(force: true);
                            });
                        });
                    }, padH: 8, padV: 2);
                NativeUguiFactory.SetLayout(btnDel.Root, minW: 54f, prefW: 58f, minH: 24f);
            }
        }
    }

    private void RebuildLocalModUploadList()
    {
        if (_localUploadListContainer == null) return;
        ClearChildren(_localUploadListContainer);

        var locals = ModContentSyncClient.DiscoverLocalMods();
        if (locals.Count == 0)
        {
            _ui.CreateText("Empty", _localUploadListContainer, "本地目录暂未扫描到 Mod，可直接把 Mod 文件夹放到 BepInEx/mods/ 或在下方填路径上传。", 12f, NativeUguiFactory.ColMuted);
            return;
        }

        int show = Math.Min(locals.Count, 12);
        for (int i = 0; i < show; i++)
        {
            var captured = locals[i];
            var row = _ui.CreateHorizontalRow("LocalMod_" + i, _localUploadListContainer, spacing: 6f);
            string kind = captured.IsZipFile ? "[压缩包]" : "[目录]";
            var txt = _ui.CreateText("Txt", row.transform,
                $"{kind} {captured.DisplayName}（{captured.ModId} v{captured.Version}）",
                12.5f, NativeUguiFactory.ColCream, wrap: true);
            NativeUguiFactory.SetLayout(txt.gameObject, flexW: 1f, minH: 24f);

            var btnUp = _ui.CreateButton("Upload", row.transform, "上传到服务器", 12f,
                NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white,
                () => UploadLocalModPathAsync(captured.FullPath), padH: 8, padV: 2);
            NativeUguiFactory.SetLayout(btnUp.Root, minW: 96f, prefW: 104f, minH: 24f);
        }
    }

    private void UploadLocalModPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus("请先选择或填写要上传的本地 Mod 路径", isError: true);
            return;
        }
        SetStatus("正在打包并上传 Mod（服务器将自动校验，约需 2-5 秒）...", isError: false);
        _ = Task.Run(async () =>
        {
            var (ok, msg) = await ModContentSyncClient.Instance
                .UploadLocalModAsync(path, HttpClientHub.ShutdownToken).ConfigureAwait(false);
            _pendingUpdates.Enqueue(() =>
            {
                SetStatus(msg, !ok);
                if (ok)
                {
                    ShowToastBanner("Mod 上传成功，其他玩家已可见");
                    RefreshCommunityModsAsync(force: true);
                }
            });
        });
    }

    // ── 锦标赛房间 Mod 选择与成员同步 ─────────────────────────────────────────

    private void RefreshTournamentRoomModsUi()
    {
        if (_tourneyRoomModsBannerCard == null) return;

        var reqMods = _currentLobby?.RequiredMods;
        bool hasReq = reqMods != null && reqMods.Count > 0;
        _tourneyRoomModsBannerCard.SetActive(hasReq);

        if (hasReq && _tourneyRoomModsListContainer != null)
        {
            ClearChildren(_tourneyRoomModsListContainer);
            if (_tourneyRoomModsStatusText != null)
            {
                bool accept = BazaarLauncherPlugin.AcceptServerMods == null || BazaarLauncherPlugin.AcceptServerMods.Value;
                _tourneyRoomModsStatusText.text = accept
                    ? $"房主已为本场选择了 {reqMods.Count} 个 Mod（系统会自动拉取贴图和文案，也可下载到本地）"
                    : $"房主已选择 {reqMods.Count} 个 Mod，但你当前关闭了「允许接收服务器下发的 Mod」";
            }

            foreach (var rm in reqMods)
            {
                var captured = rm;
                var row = _ui.CreateHorizontalRow("RoomMod_" + captured.ModId, _tourneyRoomModsListContainer, spacing: 6f);
                string avail = captured.AvailableOnServer ? "" : " [服务器缺失]";
                var txt = _ui.CreateText("Txt", row.transform,
                    $"· {captured.Name} v{captured.Version}（{captured.ModId}）· 作者 {captured.Author} · 卡 {captured.CustomCardCount} / 贴图 {captured.AssetCount}{avail}",
                    12.5f, captured.AvailableOnServer ? NativeUguiFactory.ColCream : NativeUguiFactory.ColErrorText, wrap: true);
                NativeUguiFactory.SetLayout(txt.gameObject, flexW: 1f, minH: 24f);

                if (captured.AvailableOnServer)
                {
                    var btnDl = _ui.CreateButton("DlOne", row.transform, "下载到本地", 12f,
                        NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
                        {
                            _ = Task.Run(async () =>
                            {
                                var (ok, msg, _, _) = await ModContentSyncClient.Instance
                                    .DownloadModToLocalAsync(captured.ModId, HttpClientHub.ShutdownToken).ConfigureAwait(false);
                                _pendingUpdates.Enqueue(() => SetStatus(msg, !ok));
                            });
                        }, padH: 8, padV: 2);
                    NativeUguiFactory.SetLayout(btnDl.Root, minW: 84f, prefW: 90f, minH: 24f);
                }
            }
        }

        RefreshTournamentHostModPickerUi();
    }

    private void RefreshTournamentHostModPickerUi()
    {
        if (_tourneyHostModPickerContainer == null) return;
        ClearChildren(_tourneyHostModPickerContainer);

        var allMods = ModContentSyncClient.Instance.GetCommunityModsSnapshot();
        if (allMods.Count == 0)
        {
            _ui.CreateText("Hint", _tourneyHostModPickerContainer, "点击上方「刷新可选 Mod」加载服务器社区 Mod 列表。", 12.5f, NativeUguiFactory.ColMuted);
            return;
        }

        _lobbyRules.EnsureCollections();
        var selectedSet = new HashSet<string>(_lobbyRules.SelectedModIds, StringComparer.OrdinalIgnoreCase);

        int limit = Math.Min(allMods.Count, 20);
        for (int i = 0; i < limit; i++)
        {
            var captured = allMods[i];
            bool isChecked = selectedSet.Contains(captured.ModId);
            string tag = captured.IsServerDefault ? "[全服已启]" : "[社区]";
            string label = $"{tag} {captured.Name} v{captured.Version}（{captured.ModId} · 卡 {captured.CustomCardCount} / 事件 {captured.CustomEventCount}）";

            _ui.CreateToggle("HostMod_" + captured.ModId, _tourneyHostModPickerContainer, label, isChecked, on =>
            {
                _lobbyRules.EnsureCollections();
                _lobbyRules.SelectedModIds.RemoveAll(id => string.Equals(id, captured.ModId, StringComparison.OrdinalIgnoreCase));
                if (on) _lobbyRules.SelectedModIds.Add(captured.ModId);
                _lobbyRules.RefreshPowerFlag();
                _lobbyDirty = true;
                RefreshSummaryIndicators();
            });
        }
    }

    private void AutoSyncCurrentLobbyModsIfNeeded(LobbyDetailSnapshot detail)
    {
        var ids = detail?.RequiredMods?.Where(m => m.AvailableOnServer).Select(m => m.ModId).ToList();
        if (ids == null || ids.Count == 0)
        {
            // 退出 Mod 房间或房主清空房内 Mod 时，立刻卸载旧房 Mod 视觉/文本句柄，杜绝泄漏到后续标准局
            if (!string.IsNullOrEmpty(_lastAutoSyncedRoomModSignature) || !string.IsNullOrEmpty(_autoSyncRoomModInFlightSig))
            {
                _lastAutoSyncedRoomModSignature = string.Empty;
                _autoSyncRoomModInFlightSig = string.Empty;
                ModContentSyncClient.Instance.ClearSyncedRoomBundleMods();
            }
            return;
        }
        if (BazaarLauncherPlugin.AcceptServerMods != null && !BazaarLauncherPlugin.AcceptServerMods.Value)
            return;

        string sig = detail.LobbyId + "|" + string.Join(",", ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        if (string.Equals(_lastAutoSyncedRoomModSignature, sig, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_autoSyncRoomModInFlightSig, sig, StringComparison.OrdinalIgnoreCase))
            return;

        // 同步完成前仅写入进行中签名，成功后再提交 _lastAutoSyncedRoomModSignature，确保网络瞬断失败后能自动重试
        _autoSyncRoomModInFlightSig = sig;
        _ = Task.Run(async () =>
        {
            bool ok = false;
            string summary = string.Empty;
            try
            {
                (ok, summary) = await ModContentSyncClient.Instance
                    .SyncSelectedModBundleAsync(ids, force: false, HttpClientHub.ShutdownToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ok = false;
                summary = ex.Message;
            }
            finally
            {
                _pendingUpdates.Enqueue(() =>
                {
                    if (string.Equals(_autoSyncRoomModInFlightSig, sig, StringComparison.OrdinalIgnoreCase))
                    {
                        _autoSyncRoomModInFlightSig = string.Empty;
                    }
                    if (ok)
                    {
                        _lastAutoSyncedRoomModSignature = sig;
                        ShowToastBanner(summary);
                    }
                });
            }
        });
    }

    private void SyncCurrentLobbyRequiredModsAsync(bool force)
    {
        var ids = _currentLobby?.RequiredMods?.Where(m => m.AvailableOnServer).Select(m => m.ModId).ToList();
        if (ids == null || ids.Count == 0)
        {
            SetStatus("当前房间未选用额外 Mod", isError: false);
            return;
        }
        SetStatus($"正在同步本房 {ids.Count} 个 Mod 资源...", isError: false);
        _ = Task.Run(async () =>
        {
            var (ok, summary) = await ModContentSyncClient.Instance
                .SyncSelectedModBundleAsync(ids, force, HttpClientHub.ShutdownToken).ConfigureAwait(false);
            _pendingUpdates.Enqueue(() =>
            {
                SetStatus(summary, !ok);
                if (ok) ShowToastBanner(summary);
            });
        });
    }

    private void DownloadAllCurrentLobbyModsAsync()
    {
        var ids = _currentLobby?.RequiredMods?.Where(m => m.AvailableOnServer).Select(m => m.ModId).ToList();
        if (ids == null || ids.Count == 0)
        {
            SetStatus("当前房间没有可下载的 Mod", isError: false);
            return;
        }
        SetStatus($"正在下载本房 {ids.Count} 个 Mod 到本地...", isError: false);
        _ = Task.Run(async () =>
        {
            int okCount = 0;
            string lastErr = string.Empty;
            foreach (string id in ids)
            {
                var (ok, msg, _, _) = await ModContentSyncClient.Instance
                    .DownloadModToLocalAsync(id, HttpClientHub.ShutdownToken).ConfigureAwait(false);
                if (ok) okCount++;
                else lastErr = msg;
            }
            _pendingUpdates.Enqueue(() =>
            {
                if (okCount == ids.Count)
                {
                    SetStatus($"本房 {okCount} 个 Mod 已全部下载到本地 {ModContentSyncClient.LocalModsDownloadRoot}", isError: false);
                    ShowToastBanner($"已下载 {okCount} 个 Mod 到本地");
                }
                else
                {
                    SetStatus($"成功下载 {okCount}/{ids.Count} 个 Mod：{lastErr}", isError: true);
                }
                RebuildLocalModUploadList();
            });
        });
    }
}
